// <copyright file="ReliabilityPipelineOfTTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="ReliabilityPipeline{T}"/> treats a result that <see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/>
/// rejects as a failure: the strategy, the callback and the delay apply, the policy does not, the rejected result is
/// disposed after the callback, and the last result is returned when no retries remain.
/// </summary>
[TestClass]
public class ReliabilityPipelineOfTTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task A503ResultIsRetriedAndA200ResultIsNot()
    {
        var responses = new ResponseSequence(503, 200, 503);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3));

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(2, responses.Invocations);
    }

    [TestMethod]
    public async Task A200ResultOnTheFirstAttemptIsReturnedWithoutRetry()
    {
        var responses = new ResponseSequence(200, 503);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3));

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreSame(responses.Returned[0], result);
        Assert.AreEqual(1, responses.Invocations);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task EachRejectedResultIsDisposedAfterTheCallback()
    {
        var responses = new ResponseSequence(503, 503, 200);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(
            new Count(3),
            args =>
            {
                var response = (Response)args.Result!;
                responses.Log.Add($"callback {args.AttemptNumber}, disposed: {response.Disposed}");
                return ValueTask.CompletedTask;
            });

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreSequenceEqual(
            new[] { "callback 1, disposed: False", "dispose 1", "callback 2, disposed: False", "dispose 2" },
            responses.Log);
        Assert.IsTrue(responses.Returned[0].Disposed);
        Assert.IsTrue(responses.Returned[1].Disposed);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task WhenNoRetriesRemain_TheLastResultIsReturnedAndNotDisposed()
    {
        var responses = new ResponseSequence(503, 503, 503);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3));

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreSame(responses.Returned[2], result);
        Assert.AreEqual(503, result.StatusCode);
        Assert.IsFalse(result.Disposed);
        Assert.IsTrue(responses.Returned[0].Disposed);
        Assert.IsTrue(responses.Returned[1].Disposed);
    }

    [TestMethod]
    public async Task TheCallbackGetsTheResultAndNoException()
    {
        var responses = new ResponseSequence(503, 200);
        var calls = new List<RetryEventArgs>();
        ReliabilityPipeline<Response> pipeline = CreatePipeline(
            new Count(3),
            args =>
            {
                calls.Add(args);
                return ValueTask.CompletedTask;
            });

        await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        RetryEventArgs call = Assert.ContainsSingle(calls);
        Assert.AreSame(responses.Returned[0], call.Result);
        Assert.IsNull(call.LastException);
        Assert.AreEqual(1, call.AttemptNumber);
        Assert.AreEqual(2, call.NextAttemptNumber);
    }

    [TestMethod]
    public async Task A429ResultWithRetryAfterOf5Seconds_GivesADelayOf5Seconds()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan fiveSeconds = TimeSpan.FromSeconds(5);
        using var tooManyRequests = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        tooManyRequests.Headers.RetryAfter = new RetryConditionHeaderValue(fiveSeconds);
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<HttpResponseMessage>(new ReliabilityPipelineOptions<HttpResponseMessage>
        {
            Strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, TimeSpan.FromSeconds(1)), fake),
            ShouldRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = fake,
        });

        Task<HttpResponseMessage> call = pipeline.ExecuteAsync(
            _ => new ValueTask<HttpResponseMessage>(Interlocked.Increment(ref invocations) == 1 ? tooManyRequests : ok)).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { fiveSeconds }, fake.Delays);
        fake.Advance(fiveSeconds - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        fake.Advance(TimeSpan.FromTicks(1));

        Assert.AreSame(ok, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task ThePolicyIsNotConsultedForAResultThatIsAFailure()
    {
        var responses = new ResponseSequence(503, 200);
        var policy = new RecordingRetryPolicy(false);
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(3),
            Policy = policy,
            ShouldRetryResult = IsServerError,
        });

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task AnExceptionIsRetriedOnlyWhenThePolicyAllowsIt()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(false);
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Policy = policy,
            ShouldRetryResult = _ => true,
        });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(thrown, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task WhenShouldRetryResultIsNull_NoResultIsAFailure()
    {
        var responses = new ResponseSequence(503, 200);
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response> { Strategy = new Count(3) });

        Response result = await pipeline.ExecuteAsync(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(503, result.StatusCode);
        Assert.AreEqual(1, responses.Invocations);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task AnExceptionFromShouldRetryResultStopsTheCall()
    {
        var responses = new ResponseSequence(503, 200);
        var predicateFailure = new FormatException("The predicate failed");
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(3),
            ShouldRetryResult = _ => throw predicateFailure,
        });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.AreSame(predicateFailure, thrown);
        Assert.AreEqual(1, responses.Invocations);
    }

    [TestMethod]
    public async Task AResultIsDisposedWhenShouldRetryResultThrows()
    {
        var responses = new ResponseSequence(503, 200);
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(3),
            ShouldRetryResult = _ => throw new FormatException("The predicate failed"),
        });

        await Assert.ThrowsExactlyAsync<FormatException>(() => pipeline.ExecuteAsync(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheCallerCancelsBeforeTheRetry()
    {
        using var cts = new CancellationTokenSource();
        var responses = new ResponseSequence(503, 200);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                ct =>
                {
                    cts.Cancel();
                    return responses.NextAsync(ct);
                },
                cts.Token).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheTotalTimeoutExpiresBeforeTheRetry()
    {
        RecordingFakeTimeProvider fake = new();
        var responses = new ResponseSequence(503, 200);
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(3),
            ShouldRetryResult = IsServerError,
            TotalTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = fake,
        });

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(
            () => pipeline.ExecuteAsync(
                ct =>
                {
                    fake.Advance(TimeSpan.FromSeconds(2));
                    return responses.NextAsync(ct);
                }).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheRetryCallbackThrows()
    {
        var responses = new ResponseSequence(503, 200);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3), _ => throw new FormatException("The callback failed"));

        await Assert.ThrowsExactlyAsync<FormatException>(() => pipeline.ExecuteAsync(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheStrategyThrows()
    {
        var responses = new ResponseSequence(503, 200);
        ReliabilityPipeline<Response> pipeline = CreatePipeline(RetryStrategies.FromDelegate(_ => throw new FormatException("The strategy failed")));

        await Assert.ThrowsExactlyAsync<FormatException>(() => pipeline.ExecuteAsync(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheStateChangeCallbackOfTheBreakerThrows()
    {
        var responses = new ResponseSequence(503, 503, 200);
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            MinimumThroughput = 2,
            FailureRatio = 1,
            OnStateChanged = change => change.To == CircuitState.Open ? throw new FormatException("The callback failed") : ValueTask.CompletedTask,
        });
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(3),
            ShouldRetryResult = IsServerError,
            CircuitBreaker = breaker,
        });

        await Assert.ThrowsExactlyAsync<FormatException>(() => pipeline.ExecuteAsync(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.HasCount(2, responses.Returned);
        Assert.IsTrue(responses.Returned.TrueForAll(r => r.Disposed));
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task ARejectedResultIsRetriedAfterTheDelayOfTheStrategy()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan oneHour = TimeSpan.FromHours(1);
        var responses = new ResponseSequence(503, 200);
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Linear(oneHour, 3),
            ShouldRetryResult = IsServerError,
            TimeProvider = fake,
        });

        Task<Response> call = pipeline.ExecuteAsync(responses.NextAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreEqual(1, responses.Invocations);
        fake.Advance(oneHour);

        Response result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSequenceEqual(new[] { oneHour }, fake.Delays);
    }

    [TestMethod]
    public void ExecuteAsyncRejectsANullOperation()
    {
        ReliabilityPipeline<Response> pipeline = CreatePipeline(new Count(3));

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => pipeline.ExecuteAsync(null!).AsTask());

        Assert.AreEqual("operation", thrown.ParamName);
    }

    private static bool IsServerError(Response response) => response.StatusCode >= 500;

    private static ReliabilityPipeline<Response> CreatePipeline(IRetryStrategy strategy, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        return new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = strategy,
            ShouldRetryResult = IsServerError,
            OnRetry = onRetry,
        });
    }

    // A disposable result with a status code. It writes its disposal to the log of its sequence.
    private sealed class Response(int statusCode, int attempt, List<string> log) : IDisposable
    {
        public int StatusCode => statusCode;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            this.Disposed = true;
            log.Add($"dispose {attempt}");
        }
    }

    // Returns a new response with the next status code on each attempt. The operations complete synchronously.
    private sealed class ResponseSequence(params int[] statusCodes)
    {
        public List<string> Log { get; } = [];

        public List<Response> Returned { get; } = [];

        public int Invocations => this.Returned.Count;

        public ValueTask<Response> NextAsync(CancellationToken cancellationToken)
        {
            var response = new Response(statusCodes[this.Returned.Count], this.Returned.Count + 1, this.Log);
            this.Returned.Add(response);
            return new ValueTask<Response>(response);
        }
    }
}
