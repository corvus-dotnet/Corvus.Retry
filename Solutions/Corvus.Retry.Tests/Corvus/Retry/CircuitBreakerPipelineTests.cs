// <copyright file="CircuitBreakerPipelineTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// In a <see cref="ReliabilityPipeline"/>, the circuit breaker is inside the retry loop and outside the attempt timeout: the
/// strategy and the policy receive a <see cref="BrokenCircuitException"/> as the failure of a rejected attempt, the
/// breaker counts each retry as one call, a rejected attempt starts no attempt timer, and
/// <see cref="RetryStrategies.RetryAfter(IRetryStrategy, TimeProvider?)"/> waits for the end of the break.
/// </summary>
[TestClass]
public class CircuitBreakerPipelineTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    private readonly RecordingFakeTimeProvider fake = new();

    [TestMethod]
    public async Task ARetryReceivesTheBrokenCircuitExceptionFromTheBreaker()
    {
        CircuitBreaker breaker = this.NewOpenBreaker();
        var policy = new RecordingRetryPolicy(true);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), Policy = policy, CircuitBreaker = breaker, TimeProvider = this.fake });

        BrokenCircuitException thrown = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        // Three attempts, all rejected. The policy is not consulted for the last attempt, because the strategy refuses.
        Assert.AreEqual(0, invocations);
        Assert.HasCount(2, policy.Consulted);
        Assert.IsInstanceOfType<BrokenCircuitException>(policy.Consulted[0]);
        Assert.AreEqual(BreakDuration, thrown.RetryAfter);

        // A rejection is not a call: the counts are still those of the two failures that opened the circuit.
        Assert.AreEqual((2, 2), breaker.GetCounts());
    }

    [TestMethod]
    public async Task TheRetryCallbackReceivesTheRejection()
    {
        CircuitBreaker breaker = this.NewOpenBreaker();
        RetryEventArgs? args = null;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(2),
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
            OnRetry = a =>
            {
                args = a;
                return ValueTask.CompletedTask;
            },
        });

        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(1)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.IsNotNull(args);
        Assert.IsInstanceOfType<BrokenCircuitException>(args.LastException);
        Assert.AreEqual(1, args.AttemptNumber);
    }

    [TestMethod]
    public async Task ThePolicyCanRefuseToRetryARejection()
    {
        CircuitBreaker breaker = this.NewOpenBreaker();
        int rejections = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(3),
            Policy = RetryPolicies.TransientHttp,
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
            OnRetry = _ =>
            {
                rejections++;
                return ValueTask.CompletedTask;
            },
        });

        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(1)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(0, rejections);
    }

    [TestMethod]
    public async Task TheBreakerCountsEachRetryAsOneCall()
    {
        CircuitBreaker breaker = this.NewBreaker();
        var op = new FlakyOperation(2);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), CircuitBreaker = breaker, TimeProvider = this.fake });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-3", result);
        Assert.AreEqual((3, 2), breaker.GetCounts());
    }

    [TestMethod]
    public async Task TheBreakerCountsEachRejectedResultAsAFailedCall()
    {
        CircuitBreaker breaker = this.NewBreaker();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int>
        {
            Strategy = new Count(3),
            ShouldRetryResult = static r => r < 3,
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        int result = await pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(3, result);
        Assert.AreEqual((3, 2), breaker.GetCounts());
    }

    [TestMethod]
    public async Task ARetryCanOpenTheCircuitAndThenReceiveItsRejection()
    {
        CircuitBreaker breaker = this.NewBreaker(minimumThroughput: 2, failureRatio: 1);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(4), CircuitBreaker = breaker, TimeProvider = this.fake });

        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        // Two failures open the circuit. The breaker rejects the third and the fourth attempts.
        Assert.AreEqual(2, op.Invocations);
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task ARejectedAttemptStartsNoAttemptTimer()
    {
        CircuitBreaker breaker = this.NewBreaker();
        breaker.Isolate();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromSeconds(1),
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(1)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(0, this.fake.TimersCreated);
    }

    [TestMethod]
    public async Task TheRetryAfterStrategyWaitsForTheEndOfTheBreak()
    {
        CircuitBreaker breaker = this.NewOpenBreaker();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(1, TimeSpan.FromMilliseconds(100)), this.fake),
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        Task<int> call = pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { BreakDuration }, this.fake.Delays);
        this.fake.Advance(BreakDuration);

        Assert.AreEqual(1, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task ACancellationByTheCallerDuringARetryDoesNotCount()
    {
        CircuitBreaker breaker = this.NewBreaker();
        using CancellationTokenSource cts = new();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), CircuitBreaker = breaker, TimeProvider = this.fake });

        Task<string> call = pipeline.ExecuteAsync(
            async ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    throw new InvalidOperationException("Failure");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return "never";
            },
            cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(2, invocations);
        Assert.AreEqual((1, 1), breaker.GetCounts());
    }

    private CircuitBreaker NewBreaker(int minimumThroughput = 10, double failureRatio = 0.5)
    {
        return new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = failureRatio,
            MinimumThroughput = minimumThroughput,
            BreakDuration = BreakDuration,
            TimeProvider = this.fake,
        });
    }

    private CircuitBreaker NewOpenBreaker()
    {
        CircuitBreaker breaker = this.NewBreaker(minimumThroughput: 2, failureRatio: 1);
        for (int i = 0; i < 2; i++)
        {
            Assert.IsTrue(breaker.TryAcquire(out bool probe, out _, out _));
            _ = breaker.OnFailure(new InvalidOperationException(), probe);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        return breaker;
    }
}
