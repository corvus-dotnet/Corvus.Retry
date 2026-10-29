// <copyright file="RetryEngineTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The engine that runs every retry loop: it checks the caller's token before each attempt, applies the attempt
/// timeout and the total timeout, classifies a cancellation by its source (caller, then total timeout, then attempt
/// timeout), awaits the retry callback before the delay, and lets the caller's token and the total timeout cancel a
/// delay.
/// </summary>
/// <remarks>
/// Each test passes its own time provider to the engine, so these tests run in parallel with others. An operation
/// that must wait for a timeout waits for a real infinite delay that only its token ends.
/// </remarks>
[TestClass]
public class RetryEngineTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    [TestMethod]
    public async Task WhenEveryAttemptTimesOut_EachAttemptGetsItsOwnCancelledTokenAndTheLastTimeoutIsThrown()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var tokens = new List<CancellationToken>();
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            policy,
            attemptTimeout: attemptTimeout);

        for (int timers = 1; timers <= 3; timers++)
        {
            await fake.WaitForTimersAsync(timers).ConfigureAwait(false);
            Assert.IsFalse(call.IsCompleted);
            fake.Advance(attemptTimeout);
        }

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.HasCount(3, tokens);
        Assert.AreNotEqual(tokens[0], tokens[1]);
        Assert.AreNotEqual(tokens[1], tokens[2]);
        Assert.AreNotEqual(tokens[0], tokens[2]);
        Assert.IsTrue(tokens.TrueForAll(t => t.IsCancellationRequested));

        // The inner exception is the cancellation of the last attempt.
        TaskCanceledException inner = Assert.IsInstanceOfType<TaskCanceledException>(thrown.InnerException);
        Assert.AreEqual(tokens[2], inner.CancellationToken);

        // The policy can retry an attempt timeout. The strategy has no tries left after the third.
        Assert.HasCount(2, policy.Consulted);
        for (int i = 0; i < 2; i++)
        {
            RetryTimeoutException consulted = Assert.IsInstanceOfType<RetryTimeoutException>(policy.Consulted[i]);
            Assert.AreEqual(attemptTimeout, consulted.Timeout);
            Assert.AreEqual(tokens[i], Assert.IsInstanceOfType<TaskCanceledException>(consulted.InnerException).CancellationToken);
        }

        Assert.AreNotSame(policy.Consulted[0], policy.Consulted[1]);
    }

    [TestMethod]
    public async Task WhenThePolicyRefusesAnAttemptTimeout_TheTimeoutIsThrownAfterOneAttempt()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        int invocations = 0;

        Task<string> call = RunAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            new RecordingRetryPolicy(ex => ex is not TimeoutException),
            attemptTimeout: attemptTimeout);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(1, invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringAnAttempt_TheAttemptIsCancelledAndNotRetried()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        var tokens = new List<CancellationToken>();
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            policy,
            totalTimeout: totalTimeout);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);

        // The inner exception is the cancellation of the attempt that the total timeout stopped.
        CancellationToken token = Assert.ContainsSingle(tokens);
        Assert.AreEqual(token, Assert.IsInstanceOfType<TaskCanceledException>(thrown.InnerException).CancellationToken);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringADelay_TheDelayIsCancelledAndNoFurtherAttemptStarts()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(200);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), totalTimeout: totalTimeout);

        // One timer for the total timeout, and one for the delay after the first failure.
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    // RetryTimeoutException.cs and RetryEngine.cs: "When the total timeout expires between attempts, the inner exception
    // ... is the exception of the last attempt". A delay is between two attempts.
    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringADelay_TheInnerExceptionIsTheLastFailure()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(200);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TotalTimeout = totalTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // One timer for the total timeout, and one for the delay after the first failure.
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    // RetryEngine.cs: "... or null if the last attempt returned a result".
    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringADelayAfterARejectedResult_TheInnerExceptionIsNull()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(200);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Linear(OneHour, 3),
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
            ShouldRetryResult = _ => true,
        });

        Task<string> call = pipeline.ExecuteAsync(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return new ValueTask<string>("rejected");
            }).AsTask();

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsNull(thrown.InnerException);
        fake.Advance(OneHour);
        Assert.AreEqual(1, invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresAtTheSameInstantAsTheDelay_TheInnerExceptionIsTheLastFailureAndNoFurtherAttemptStarts()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan timeout = TimeSpan.FromMilliseconds(200);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(timeout, 3), TotalTimeout = timeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { timeout, timeout }, fake.Delays);
        fake.Advance(timeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(timeout, thrown.Timeout);
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringAFailedAttempt_TheRetryTimeoutExceptionHasThatFailureAsItsInnerException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var failure = new InvalidOperationException("The attempt failed");
        int invocations = 0;

        Task<string> call = RunAsync<string>(
            _ =>
            {
                invocations++;
                fake.Advance(totalTimeout * 2);
                throw failure;
            },
            fake,
            new Count(3),
            totalTimeout: totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(failure, thrown.InnerException);
        Assert.AreEqual(1, invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringTheRetryCallback_TheRetryTimeoutExceptionHasTheLastFailureAsItsInnerException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The strategy has no delay, so the next attempt finds that the total timeout has expired.
        Task<string> call = RunAsync(
            _ => new ValueTask<string>(op.Invoke()),
            fake,
            new Count(3),
            totalTimeout: totalTimeout,
            onRetry: _ =>
            {
                fake.Advance(totalTimeout * 2);
                return ValueTask.CompletedTask;
            });

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelay_TheCallIsCancelledAtOnceAndNoFurtherAttemptStarts()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => WhenCompleteAsync(call)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    // Decision Q1 as it applies to RetryEngine. The engine awaits each attempt to its end, and does not wait on the token
    // instead, so a call cannot return while its attempt holds the test call of a half-open circuit. When the attempt
    // ends with the caller's cancellation, the engine releases the test call before it throws.
    [TestMethod]
    public async Task WhenTheCallerCancelsTheTestCallOfAHalfOpenCircuit_TheCallEndsWithTheAttemptAndTheNextCallMakesTheTest()
    {
        RecordingFakeTimeProvider fake = new();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, FailureRatio = 1, TimeProvider = fake });
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        fake.Advance(new CircuitBreakerOptions().BreakDuration);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The operation ignores its token until the gate opens.
        Task<string> call = pipeline.ExecuteAsync(
            async ct =>
            {
                started.SetResult();
                await gate.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return "not cancelled";
            },
            cts.Token).AsTask();

        await started.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await cts.CancelAsync().ConfigureAwait(false);

        // The cancelled call still waits for its attempt, so the attempt still holds the test call.
        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("rejected")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);

        gate.SetResult();
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WhenCompleteAsync(call)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);

        // The cancelled call released the test call before it threw, so the next call makes the test call at once.
        Assert.AreEqual("success", await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelayWithATotalTimeout_TheCallIsCancelledRatherThanTimedOut()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), totalTimeout: OneHour, cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // The caller token cancels the total timeout source too, but the engine reports the caller cancellation.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WhenCompleteAsync(call)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheTokenIsAlreadyCancelled_TheOperationIsNotInvoked()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        var op = new FlakyOperation(0);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Count(3), attemptTimeout: OneHour, totalTimeout: OneHour, cancellationToken: cts.Token)).ConfigureAwait(false);

        Assert.AreEqual(0, op.Invocations);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsAtTheInstantTheAttemptTimesOut_TheCallIsCancelledWithTheCallersTokenRatherThanTimedOut()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(NeverCompletesAsync, fake, new Count(3), policy, attemptTimeout: attemptTimeout, cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        // The operation throws the cancellation of its own token, which the attempt timeout links to the caller's token.
        // The caller sees its own token, as for a cancelled delay, so a filter on the caller's token matches.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WhenCompleteAsync(call)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutAndTheAttemptTimeoutExpireAtTheSameInstant_TheTotalTimeoutIsThrown()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan timeout = TimeSpan.FromMilliseconds(100);

        Task<string> call = RunAsync(NeverCompletesAsync, fake, new Count(3), attemptTimeout: timeout, totalTimeout: timeout);

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(timeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(timeout, thrown.Timeout);

        // The inner exception is the cancellation of the only attempt, and no attempt failed before it.
        Assert.IsInstanceOfType<TaskCanceledException>(thrown.InnerException);
    }

    [TestMethod]
    [DataRow(true, true, true, nameof(CancellationCause.Caller))]
    [DataRow(true, false, false, nameof(CancellationCause.Caller))]
    [DataRow(false, true, true, nameof(CancellationCause.TotalTimeout))]
    [DataRow(false, true, false, nameof(CancellationCause.TotalTimeout))]
    [DataRow(false, false, true, nameof(CancellationCause.AttemptTimeout))]
    [DataRow(false, false, false, nameof(CancellationCause.None))]
    public void ClassifyChoosesTheCallerThenTheTotalTimeoutThenTheAttemptTimeout(bool caller, bool total, bool attempt, string expected)
    {
        using var callerCts = new CancellationTokenSource();
        using var totalCts = new CancellationTokenSource();
        using var attemptCts = new CancellationTokenSource();
        CancelIf(caller, callerCts);
        CancelIf(total, totalCts);
        CancelIf(attempt, attemptCts);

        // The cause is internal, so the test data gives its name.
        Assert.AreEqual(Enum.Parse<CancellationCause>(expected), RetryEngine.Classify(callerCts.Token, totalCts, attemptCts));
    }

    [TestMethod]
    public void WhenThereAreNoTimeoutSources_ClassifyFindsOnlyTheCaller()
    {
        Assert.AreEqual(CancellationCause.None, RetryEngine.Classify(CancellationToken.None, null, null));
        Assert.AreEqual(CancellationCause.Caller, RetryEngine.Classify(new CancellationToken(true), null, null));
    }

    [TestMethod]
    public async Task ACancellationThatNoSourceCaused_IsPassedToThePolicyAndRetried()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1, _ => new OperationCanceledException(new CancellationToken(true)));
        var policy = new RecordingRetryPolicy(true);

        string result = await RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Count(3), policy, attemptTimeout: OneHour, totalTimeout: OneHour).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.AreSame(op.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task TheOnRetryCallbackGetsTheAttemptNumbersInOrderAndTheDelayStartsOnlyWhenItCompletes()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(2);
        var calls = new List<RetryEventArgs>();
        var firstCallback = new TaskCompletionSource();

        ValueTask OnRetry(RetryEventArgs args)
        {
            lock (calls)
            {
                calls.Add(args);
            }

            return args.AttemptNumber == 1 ? new ValueTask(firstCallback.Task) : ValueTask.CompletedTask;
        }

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 5), onRetry: OnRetry);

        // The engine waits for the first callback, so no delay has started.
        Assert.HasCount(1, calls);
        Assert.AreEqual(0, fake.TimersCreated);
        firstCallback.SetResult();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(OneHour);
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(OneHour);

        Assert.AreEqual("attempt-3", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.HasCount(2, calls);
        Assert.AreEqual(1, calls[0].AttemptNumber);
        Assert.AreEqual(2, calls[0].NextAttemptNumber);
        Assert.AreEqual(2, calls[1].AttemptNumber);
        Assert.AreEqual(3, calls[1].NextAttemptNumber);
        Assert.AreSame(op.Thrown[0], calls[0].LastException);
        Assert.AreSame(op.Thrown[1], calls[1].LastException);
        Assert.AreEqual(OneHour, calls[0].DelayBeforeRetry);
        Assert.AreEqual(OneHour, calls[1].DelayBeforeRetry);
        Assert.AreSequenceEqual(new[] { OneHour, OneHour }, fake.Delays);
    }

    [TestMethod]
    public async Task AnExceptionFromTheOnRetryCallbackStopsTheCallBeforeTheDelay()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var callbackFailure = new FormatException("The callback failed");

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 5), onRetry: _ => throw callbackFailure)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public async Task TheStrategyGetsTheAttemptNumberTheFailureAndThePreviousDelay()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var strategy = new RecordingStrategy(TimeSpan.Zero, TimeSpan.FromTicks(-5), TimeSpan.FromMilliseconds(1));

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, strategy);

        // A zero delay and a negative delay (limited to zero) need no timer.
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(4, op.Invocations);
        Assert.HasCount(4, strategy.Attempts);
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(i + 1, strategy.Attempts[i].AttemptNumber);
            Assert.AreSame(op.Thrown[i], strategy.Attempts[i].Exception);
            Assert.IsNull(strategy.Attempts[i].Result);
        }

        Assert.AreSequenceEqual(
            new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(1) },
            strategy.Attempts.ConvertAll(a => a.PreviousDelay));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WhenTheOperationOfAPipelineThrowsAnAggregateException_ThePolicyIsAskedAboutTheExceptionsInsideItDepthFirst(bool withTimeouts)
    {
        var first = new HttpRequestException("first");
        var second = new HttpRequestException("second");
        var aggregate = new AggregateException(new AggregateException(first), second);
        var op = new FlakyOperation(1, _ => aggregate);
        var consulted = new List<Exception>();
        var strategy = new RecordingStrategy(TimeSpan.Zero, TimeSpan.Zero);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = strategy,
            Policy = RetryPolicies.FromPredicate(
                e =>
                {
                    consulted.Add(e);
                    return e is HttpRequestException;
                }),
            AttemptTimeout = withTimeouts ? OneHour : null,
            TotalTimeout = withTimeouts ? OneHour : null,
            TimeProvider = new RecordingFakeTimeProvider(),
        });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.AreSequenceEqual(new Exception[] { first, second }, consulted);
        Assert.AreSame(aggregate, Assert.ContainsSingle(strategy.Attempts).Exception);
    }

    [TestMethod]
    public async Task WhenTheStrategyRefuses_ThePolicyIsNotConsulted()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), new RecordingFakeTimeProvider(), new DoNotRetry(), policy)).ConfigureAwait(false);

        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public void WhenThereIsNoTimeoutAndTheAttemptSucceedsSynchronously_NoTimerIsCreatedAndTheCallCompletesSynchronously()
    {
        RecordingFakeTimeProvider fake = new();

        ValueTask<string> call = RetryEngine.RunAsync<TestOperation<string>, string>(
            new(_ => new ValueTask<string>("done")), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, fake, null, null, null, null, false);

        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("done", call.Result);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public async Task WhenAResultIsAFailure_ItIsRetriedAndTheCallbackGetsTheResultWithNoException()
    {
        RecordingFakeTimeProvider fake = new();
        var results = new List<object>();
        var calls = new List<RetryEventArgs>();
        var strategy = new RecordingStrategy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.Zero);
        var policy = new RecordingRetryPolicy(true);
        object good = new();

        // Each rejected result is a new instance, so the test can tell which one each callback got.
        Task<object> call = RunAsync(
            _ =>
            {
                object r = results.Count < 2 ? new object() : good;
                results.Add(r);
                return new ValueTask<object>(r);
            },
            fake,
            strategy,
            policy,
            onRetry: args =>
            {
                calls.Add(args);
                return ValueTask.CompletedTask;
            },
            isFailure: r => r != good);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(10));
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(20));

        Assert.AreSame(good, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.HasCount(3, results);
        Assert.HasCount(2, calls);
        for (int i = 0; i < 2; i++)
        {
            Assert.IsNull(calls[i].LastException);
            Assert.AreSame(results[i], calls[i].Result);
            Assert.AreEqual(i + 1, calls[i].AttemptNumber);
            Assert.AreSame(results[i], strategy.Attempts[i].Result);
            Assert.IsNull(strategy.Attempts[i].Exception);
        }

        Assert.AreEqual(TimeSpan.FromMilliseconds(10), calls[0].DelayBeforeRetry);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), calls[1].DelayBeforeRetry);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20) }, fake.Delays);

        // The policy is for exceptions only.
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheStrategyHasNoTriesLeftForAResultThatIsAFailure_ThatResultIsReturnedUndisposed()
    {
        RecordingFakeTimeProvider fake = new();
        var results = new List<DisposableResult>();

        DisposableResult result = await RetryEngine.RunAsync<TestOperation<DisposableResult>, DisposableResult>(
            new(
                _ =>
                {
                    var r = new DisposableResult();
                    results.Add(r);
                    return new ValueTask<DisposableResult>(r);
                },
                _ => true),
            CancellationToken.None,
            new Count(3),
            AnyExceptionPolicy.Default,
            fake,
            null,
            null,
            args =>
            {
                // The callback sees the rejected result before it is disposed.
                Assert.IsFalse(((DisposableResult)args.Result!).Disposed);
                return ValueTask.CompletedTask;
            },
            null,
            false).ConfigureAwait(false);

        Assert.HasCount(3, results);
        Assert.AreSame(results[2], result);
        Assert.IsTrue(results[0].Disposed);
        Assert.IsTrue(results[1].Disposed);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task TheSynchronousLoopIsCancelledDuringADelay()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The synchronous loop blocks its thread for the delay, so it runs on the thread pool.
        Task<string> call = Task.Run(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(new(op.Invoke), cts.Token, new Linear(OneHour, 3), AnyExceptionPolicy.Default, fake, null));

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WhenCompleteAsync(call)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task TheSynchronousLoopWaitsForAnOnRetryCallbackThatHasNotCompleted()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackResult = new TaskCompletionSource();

        Task<string> call = Task.Run(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(
                new(op.Invoke),
                CancellationToken.None,
                new Count(3),
                AnyExceptionPolicy.Default,
                fake,
                _ =>
                {
                    callbackStarted.SetResult();
                    return new ValueTask(callbackResult.Task);
                }));

        await callbackStarted.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsFalse(call.IsCompleted);
        callbackResult.SetResult();

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WhenTheCallbackOfTheSynchronousRetryAwaits_ACallFromASingleThreadedSynchronizationContextCompletesAndRestoresTheContext()
    {
        var op = new FlakyOperation(1);
        SynchronizationContext? contextInCallback = null;

        // The thread of the context waits for the callback, so a continuation that is posted to the context never runs.
        Task<(string Result, bool ContextIsCurrent)> call = BlockedThreadSynchronizationContext.RunOnBlockedThread(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(
                new(op.Invoke),
                CancellationToken.None,
                new Count(3),
                AnyExceptionPolicy.Default,
                new RecordingFakeTimeProvider(),
                async _ =>
                {
                    contextInCallback = SynchronizationContext.Current;
                    await Task.Yield();
                }));

        (string result, bool contextIsCurrent) = await call.WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.IsNull(contextInCallback);
        Assert.IsTrue(contextIsCurrent);
    }

    [TestMethod]
    public void TheSynchronousLoopRethrowsACancellationThatTheCallerCausedWithoutConsultingThePolicy()
    {
        using var cts = new CancellationTokenSource();
        var policy = new RecordingRetryPolicy(true);
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });

        OperationCanceledException thrown = Assert.ThrowsExactly<OperationCanceledException>(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(new(op.Invoke), cts.Token, new Count(3), policy, new RecordingFakeTimeProvider(), null));

        Assert.AreSame(op.Thrown[0], thrown);
        Assert.IsEmpty(policy.Consulted);
    }

    private static Task<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeProvider timeProvider,
        IRetryStrategy strategy,
        IRetryPolicy? policy = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? totalTimeout = null,
        Func<RetryEventArgs, ValueTask>? onRetry = null,
        CancellationToken cancellationToken = default,
        Func<T, bool>? isFailure = null)
    {
        return RetryEngine.RunAsync<TestOperation<T>, T>(
            new(operation, isFailure),
            cancellationToken,
            strategy,
            policy ?? AnyExceptionPolicy.Default,
            timeProvider,
            attemptTimeout,
            totalTimeout,
            onRetry,
            null,
            false).AsTask();
    }

    // Waits, with a time limit, until the task completes, and then awaits the task itself, so that the caller sees the
    // exact exception of the task.
    private static async Task<T> WhenCompleteAsync<T>(Task<T> task)
    {
        await Task.WhenAny(task).WaitAsync(WaitLimit).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    // A real infinite delay that only the token ends, so that no timer from the fake time provider is involved.
    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }

    private static void CancelIf(bool cancel, CancellationTokenSource source)
    {
        if (cancel)
        {
            source.Cancel();
        }
    }

    private readonly struct TestOperation<T> : IRetryOperation<T>
    {
        private readonly Func<CancellationToken, ValueTask<T>> operation;
        private readonly Func<T, bool>? isFailure;

        public TestOperation(Func<CancellationToken, ValueTask<T>> operation, Func<T, bool>? isFailure = null)
        {
            this.operation = operation;
            this.isFailure = isFailure;
        }

        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken) => this.operation(cancellationToken);

        public bool IsFailure(T result) => this.isFailure?.Invoke(result) ?? false;
    }

    private sealed class DisposableResult : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }
}
