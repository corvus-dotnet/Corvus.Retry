// <copyright file="RetryTaskFactoryOfTRunTests.cs" company="Endjin Limited">
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
/// A <see cref="RetryTaskFactory{T}"/> treats a result that <see cref="RetryTaskFactory{T}.CanRetryResult"/>
/// rejects as a failure: the strategy, the callback and the delay apply, the policy does not, the rejected result is
/// disposed after the callback, and the last result is returned when no retries remain.
/// </summary>
[TestClass]
public class RetryTaskFactoryOfTRunTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task A503ResultIsRetriedAndA200ResultIsNot()
    {
        var responses = new ResponseSequence(503, 200, 503);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(2, responses.Invocations);
    }

    [TestMethod]
    public async Task A200ResultOnTheFirstAttemptIsReturnedWithoutRetry()
    {
        var responses = new ResponseSequence(200, 503);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

        Assert.AreSame(responses.Returned[0], result);
        Assert.AreEqual(1, responses.Invocations);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task EachRejectedResultIsDisposedAfterTheCallback()
    {
        var responses = new ResponseSequence(503, 503, 200);
        RetryTaskFactory<Response> factory = CreateFactory(
            new Count(3),
            args =>
            {
                var response = (Response)args.Result!;
                responses.Log.Add($"callback {args.AttemptNumber}, disposed: {response.Disposed}");
                return ValueTask.CompletedTask;
            });

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

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
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

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
        RetryTaskFactory<Response> factory = CreateFactory(
            new Count(3),
            args =>
            {
                calls.Add(args);
                return ValueTask.CompletedTask;
            });

        await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

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
        var factory = new RetryTaskFactory<HttpResponseMessage>
        {
            Strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, TimeSpan.FromSeconds(1)), fake),
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = fake,
        };

        Task<HttpResponseMessage> call = factory.RunValueTask(
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
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            Policy = policy,
            CanRetryResult = IsServerError,
        };

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task AnExceptionIsRetriedOnlyWhenThePolicyAllowsIt()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(false);
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Policy = policy,
            CanRetryResult = _ => true,
        };

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(thrown, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task WhenCanRetryResultIsNull_NoResultIsAFailure()
    {
        var responses = new ResponseSequence(503, 200);
        var factory = new RetryTaskFactory<Response> { Strategy = new Count(3) };

        Response result = await factory.RunValueTask(responses.NextAsync).ConfigureAwait(false);

        Assert.AreEqual(503, result.StatusCode);
        Assert.AreEqual(1, responses.Invocations);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task AnExceptionFromCanRetryResultStopsTheCall()
    {
        var responses = new ResponseSequence(503, 200);
        var predicateFailure = new FormatException("The predicate failed");
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = _ => throw predicateFailure,
        };

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.AreSame(predicateFailure, thrown);
        Assert.AreEqual(1, responses.Invocations);
    }

    [TestMethod]
    public async Task AResultIsDisposedWhenCanRetryResultThrows()
    {
        var responses = new ResponseSequence(503, 200);
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = _ => throw new FormatException("The predicate failed"),
        };

        await Assert.ThrowsExactlyAsync<FormatException>(() => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheCallerCancelsBeforeTheRetry()
    {
        using var cts = new CancellationTokenSource();
        var responses = new ResponseSequence(503, 200);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => factory.RunValueTask(
                ct =>
                {
                    cts.Cancel();
                    return responses.NextAsync(ct);
                },
                cts.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheTotalTimeoutExpiresBeforeTheRetry()
    {
        RecordingFakeTimeProvider fake = new();
        var responses = new ResponseSequence(503, 200);
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = IsServerError,
            TotalTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = fake,
        };

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(
            () => factory.RunValueTask(
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
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3), _ => throw new FormatException("The callback failed"));

        await Assert.ThrowsExactlyAsync<FormatException>(() => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheStrategyThrows()
    {
        var responses = new ResponseSequence(503, 200);
        RetryTaskFactory<Response> factory = CreateFactory(RetryStrategies.FromDelegate(_ => throw new FormatException("The strategy failed")));

        await Assert.ThrowsExactlyAsync<FormatException>(() => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task ARejectedResultIsDisposedWhenTheStateChangeCallbackOfTheBreakerThrows()
    {
        var responses = new ResponseSequence(503, 503, 200);
        var breaker = new CircuitBreaker(
            failureRate: 1,
            minimumCalls: 2,
            onStateChanged: change => change.To == CircuitState.Open ? throw new FormatException("The callback failed") : ValueTask.CompletedTask);
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = IsServerError,
            CircuitBreaker = breaker,
        };

        await Assert.ThrowsExactlyAsync<FormatException>(() => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

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
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Linear(oneHour, 3),
            CanRetryResult = IsServerError,
            TimeProvider = fake,
        };

        Task<Response> call = factory.RunValueTask(responses.NextAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreEqual(1, responses.Invocations);
        fake.Advance(oneHour);

        Response result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSequenceEqual(new[] { oneHour }, fake.Delays);
    }

    [TestMethod]
    public async Task TheCallbackForARejectedResultGetsTheDelayOfTheStrategy()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan oneHour = TimeSpan.FromHours(1);
        var responses = new ResponseSequence(503, 200);
        var calls = new List<RetryEventArgs>();
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Linear(oneHour, 3),
            CanRetryResult = IsServerError,
            TimeProvider = fake,
            OnRetry = args =>
            {
                lock (calls)
                {
                    calls.Add(args);
                }

                return ValueTask.CompletedTask;
            },
        };

        Task<Response> call = factory.RunValueTask(responses.NextAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(oneHour);

        Assert.AreEqual(200, (await call.WaitAsync(WaitLimit).ConfigureAwait(false)).StatusCode);
        RetryEventArgs args = Assert.ContainsSingle(calls);
        Assert.AreEqual(oneHour, args.DelayBeforeRetry);
        Assert.AreSame(responses.Returned[0], args.Result);
        Assert.IsNull(args.LastException);
    }

    [TestMethod]
    public async Task WithTheStrategyOfTheEarlierPipelineDefault_TheGenericFactoryMakesFourAttemptsWithTwoSecondsBeforeEachRetry()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan twoSeconds = TimeSpan.FromSeconds(2);
        var responses = new ResponseSequence(503, 503, 503, 503);

        // The default strategy of a factory makes 5 tries without delay. RetryTaskFactorySettingsTests covers it. This
        // test sets the strategy that the earlier pipeline used by default: 4 attempts, with 2 seconds before each retry.
        var factory = new RetryTaskFactory<Response> { Strategy = new Linear(twoSeconds, 4), CanRetryResult = IsServerError, TimeProvider = fake };

        Task<Response> call = factory.RunValueTask(responses.NextAsync).AsTask();

        for (int timers = 1; timers <= 3; timers++)
        {
            await fake.WaitForTimersAsync(timers).ConfigureAwait(false);
            Assert.IsFalse(call.IsCompleted);
            fake.Advance(twoSeconds);
        }

        Response result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreSame(responses.Returned[3], result);
        Assert.AreEqual(4, responses.Invocations);
        Assert.AreSequenceEqual(new[] { twoSeconds, twoSeconds, twoSeconds }, fake.Delays);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WhenTheDisposalOfARejectedResultThrowsBeforeTheRetry_TheCallerGetsThatExceptionAndTheNextAttemptDoesNotStart(bool withFallback)
    {
        var disposeFailure = new FormatException("Dispose failed.");
        var responses = new ResponseSequence(503, 200) { DisposeFailure = disposeFailure };
        int actions = 0;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = IsServerError,
            Fallback = withFallback
                ? new Fallback<Response>(_ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<Response>(new Response(200, 0, []));
                })
                : null,
        };

        // The exception from the disposal is not an outcome of the attempts, so the fallback does not apply.
        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => factory.RunValueTask(responses.NextAsync).AsTask()).ConfigureAwait(false);

        Assert.AreSame(disposeFailure, thrown);
        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public void RunValueTaskRejectsANullOperation()
    {
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => factory.RunValueTask(null!).AsTask());

        Assert.AreEqual("operation", thrown.ParamName);
    }

    [TestMethod]
    public void RunRejectsANullOperation()
    {
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => factory.Run(null!));

        Assert.AreEqual("operation", thrown.ParamName);
    }

    [TestMethod]
    public async Task WithRun_A503ResultIsRetriedAndA200ResultIsNot()
    {
        var responses = new ResponseSequence(503, 200, 503);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        Response result = await factory.Run(responses.NextTaskAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(2, responses.Invocations);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task WithRun_EachRejectedResultIsDisposedAfterTheCallback()
    {
        var responses = new ResponseSequence(503, 503, 200);
        RetryTaskFactory<Response> factory = CreateFactory(
            new Count(3),
            args =>
            {
                var response = (Response)args.Result!;
                responses.Log.Add($"callback {args.AttemptNumber}, disposed: {response.Disposed}");
                return ValueTask.CompletedTask;
            });

        Response result = await factory.Run(responses.NextTaskAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSequenceEqual(
            new[] { "callback 1, disposed: False", "dispose 1", "callback 2, disposed: False", "dispose 2" },
            responses.Log);
        Assert.IsTrue(responses.Returned[0].Disposed);
        Assert.IsTrue(responses.Returned[1].Disposed);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task WithRun_WhenNoRetriesRemain_TheLastResultIsReturnedAndNotDisposed()
    {
        var responses = new ResponseSequence(503, 503, 503);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        Response result = await factory.Run(responses.NextTaskAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSame(responses.Returned[2], result);
        Assert.IsFalse(result.Disposed);
        Assert.IsTrue(responses.Returned[0].Disposed);
        Assert.IsTrue(responses.Returned[1].Disposed);
    }

    [TestMethod]
    public async Task WithRun_ARejectedResultOfAnAsynchronousOperationIsDisposedBeforeTheRetry()
    {
        var responses = new ResponseSequence(503, 200);
        RetryTaskFactory<Response> factory = CreateFactory(new Count(3));

        async Task<Response> OperationAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            responses.Log.Add($"attempt {responses.Invocations + 1}");
            return await responses.NextTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        Response result = await factory.Run(OperationAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSequenceEqual(new[] { "attempt 1", "dispose 1", "attempt 2" }, responses.Log);
    }

    [TestMethod]
    public async Task WithRun_ThePolicyIsNotConsultedForAResultThatIsAFailure()
    {
        var responses = new ResponseSequence(503, 200);
        var policy = new RecordingRetryPolicy(false);
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            Policy = policy,
            CanRetryResult = IsServerError,
        };

        Response result = await factory.Run(responses.NextTaskAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WithRun_ARejectedResultIsDisposedWhenCanRetryResultThrows()
    {
        var responses = new ResponseSequence(503, 200);
        var predicateFailure = new FormatException("The predicate failed");
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(3),
            CanRetryResult = _ => throw predicateFailure,
        };

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => factory.Run(responses.NextTaskAsync)).ConfigureAwait(false);

        Assert.AreSame(predicateFailure, thrown);
        Assert.IsTrue(Assert.ContainsSingle(responses.Returned).Disposed);
    }

    [TestMethod]
    public async Task WithRun_TheFallbackReceivesTheLastRejectedResult()
    {
        var responses = new ResponseSequence(503, 503);
        FallbackEventArgs<Response>? received = null;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(2),
            CanRetryResult = IsServerError,
            Fallback = new(_ => new ValueTask<Response>(new Response(200, 0, [])))
            {
                AppliesToResult = IsServerError,
                OnFallback = args =>
                {
                    received = args;
                    return ValueTask.CompletedTask;
                },
            },
        };

        Response result = await factory.Run(responses.NextTaskAsync).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSame(responses.Returned[1], received!.Value.Result);
        Assert.IsTrue(responses.Returned[0].Disposed);
        Assert.IsTrue(responses.Returned[1].Disposed);
    }

    [TestMethod]
    public async Task WithRun_AnOperationThatReturnsANullTaskFailsTheAttempt()
    {
        int invocations = 0;
        var factory = new RetryTaskFactory<string> { Strategy = new Count(3) };

        string result = await factory.Run(_ => Interlocked.Increment(ref invocations) == 1 ? null! : Task.FromResult("ok")).WaitAsync(WaitLimit).ConfigureAwait(false);

        // The null task is a failure that the policy accepts, so the attempt is retried.
        Assert.AreEqual("ok", result);
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    public void RunAndRunValueTaskOfAFactoryForATaskThrowBeforeTheOperationRuns()
    {
        int invocations = 0;
        var factory = new RetryTaskFactory<Task<int>>();

        NotSupportedException fromRun = Assert.ThrowsExactly<NotSupportedException>(
            () => factory.Run(_ =>
            {
                invocations++;
                return Task.FromResult(Task.FromResult(1));
            }));
        NotSupportedException fromRunValueTask = Assert.ThrowsExactly<NotSupportedException>(
            () => factory.RunValueTask(_ =>
            {
                invocations++;
                return new ValueTask<Task<int>>(Task.FromResult(1));
            }));

        Assert.AreEqual(0, invocations);
        Assert.AreEqual(
            "RetryTask<T> cannot retry a function that returns a task: the task completes when the function returns, before the operation ends. Use the result type of the task as T with Run or RunValueTask, or use Retriable.RetryAsync.",
            fromRun.Message);
        Assert.AreEqual(fromRun.Message, fromRunValueTask.Message);
    }

    [TestMethod]
    public async Task WithRun_TheFirstAttemptRunsOnTheCallersThreadUntilItsFirstAwait()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int attemptThread = 0;
        var factory = new RetryTaskFactory<string>();

        Task<string> call = factory.Run(_ =>
        {
            attemptThread = Environment.CurrentManagedThreadId;
            return Task.FromResult("ok");
        });

        // The attempt ran before Run returned, on this thread.
        Assert.AreEqual(callerThread, attemptThread);
        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("ok", await call.ConfigureAwait(false));
    }

    private static bool IsServerError(Response response) => response.StatusCode >= 500;

    private static RetryTaskFactory<Response> CreateFactory(IRetryStrategy strategy, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        return new RetryTaskFactory<Response>
        {
            Strategy = strategy,
            CanRetryResult = IsServerError,
            OnRetry = onRetry,
        };
    }

    // A disposable result with a status code. It writes its disposal to the log of its sequence.
    // When DisposeFailure is set, Dispose throws it after it records the disposal.
    private sealed class Response(int statusCode, int attempt, List<string> log) : IDisposable
    {
        public int StatusCode => statusCode;

        public bool Disposed { get; private set; }

        public Exception? DisposeFailure { get; init; }

        public void Dispose()
        {
            this.Disposed = true;
            log.Add($"dispose {attempt}");
            if (this.DisposeFailure is { } failure)
            {
                throw failure;
            }
        }
    }

    // Returns a new response with the next status code on each attempt. The operations complete synchronously.
    private sealed class ResponseSequence(params int[] statusCodes)
    {
        public List<string> Log { get; } = [];

        public List<Response> Returned { get; } = [];

        public int Invocations => this.Returned.Count;

        // The exception that the Dispose method of each response throws, or null.
        public Exception? DisposeFailure { get; init; }

        public ValueTask<Response> NextAsync(CancellationToken cancellationToken)
        {
            var response = new Response(statusCodes[this.Returned.Count], this.Returned.Count + 1, this.Log) { DisposeFailure = this.DisposeFailure };
            this.Returned.Add(response);
            return new ValueTask<Response>(response);
        }

        public Task<Response> NextTaskAsync(CancellationToken cancellationToken)
        {
            return this.NextAsync(cancellationToken).AsTask();
        }
    }
}
