// <copyright file="FallbackTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The fallback of a <see cref="ReliabilityPipeline{T}"/> is the outermost part of the pipeline: it gives a value for an
/// exception that its policy accepts (including a <see cref="RetryTimeoutException"/> from the total timeout and a
/// <see cref="BrokenCircuitException"/>) and for a result that <see cref="FallbackOptions{T}.ShouldFallbackResult"/>
/// rejects, it awaits <see cref="FallbackOptions{T}.OnFallback"/> before <see cref="FallbackOptions{T}.Action"/>, and it
/// never applies when the caller cancels.
/// </summary>
[TestClass]
public class FallbackTests
{
    private const string FallbackValue = "fallback";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task AfterTheLastRetryFails_ThePipelineReturnsTheFallbackValue()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(calls),
        });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(3, op.Invocations);
        FallbackArgs<string> args = Assert.ContainsSingle(calls);
        Assert.AreSame(op.LastThrown, args.Exception);
        Assert.IsNull(args.Result);
    }

    [TestMethod]
    public async Task WhenTheOperationSucceeds_TheFallbackDoesNotApply()
    {
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(calls),
        });

        ValueTask<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>("success"));

        // An operation that completes synchronously gives a call that completes synchronously.
        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("success", await call.ConfigureAwait(false));
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task ARejectedResult_GivesTheFallbackValueAndIsDisposedBeforeOnFallbackAndAction()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        FallbackArgs<Response>? received = null;
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = new FallbackOptions<Response>
            {
                ShouldFallbackResult = r => r.StatusCode >= 500,
                OnFallback = args =>
                {
                    received = args;
                    log.Add($"on fallback, disposed: {args.Result!.Disposed}");
                    return ValueTask.CompletedTask;
                },
                Action = _ =>
                {
                    log.Add("action");
                    return new ValueTask<Response>(new Response(200, log));
                },
            },
        });

        Response result = await pipeline.ExecuteAsync(_ => new ValueTask<Response>(rejected)).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsFalse(result.Disposed);
        Assert.AreSequenceEqual(new[] { "dispose 503", "on fallback, disposed: True", "action" }, log);
        Assert.IsNotNull(received);
        Assert.AreSame(rejected, received.Value.Result);
        Assert.IsNull(received.Value.Exception);
    }

    [TestMethod]
    public async Task AResultThatShouldFallbackResultAccepts_IsReturnedAndNotDisposed()
    {
        var log = new List<string>();
        var ok = new Response(200, log);
        var calls = new List<FallbackArgs<Response>>();
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, new Response(0, log)) with { ShouldFallbackResult = r => r.StatusCode >= 500 },
        });

        Response result = await pipeline.ExecuteAsync(_ => new ValueTask<Response>(ok)).ConfigureAwait(false);

        Assert.AreSame(ok, result);
        Assert.IsFalse(ok.Disposed);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenShouldFallbackResultIsNull_NoResultGetsTheFallbackValue()
    {
        var log = new List<string>();
        var serverError = new Response(503, log);
        var calls = new List<FallbackArgs<Response>>();
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, new Response(200, log)),
        });

        Response result = await pipeline.ExecuteAsync(_ => new ValueTask<Response>(serverError)).ConfigureAwait(false);

        Assert.AreSame(serverError, result);
        Assert.IsFalse(serverError.Disposed);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenNoRetriesRemainForARejectedResult_TheFallbackReceivesTheLastResult()
    {
        var log = new List<string>();
        var responses = new List<Response>();
        var calls = new List<FallbackArgs<Response>>();
        var pipeline = new ReliabilityPipeline<Response>(new ReliabilityPipelineOptions<Response>
        {
            Strategy = new Count(2),
            ShouldRetryResult = r => r.StatusCode >= 500,
            Fallback = RecordingFallback(calls, new Response(200, log)) with { ShouldFallbackResult = r => r.StatusCode >= 500 },
        });

        Response result = await pipeline.ExecuteAsync(
            _ =>
            {
                var response = new Response(503, log);
                responses.Add(response);
                return new ValueTask<Response>(response);
            }).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.HasCount(2, responses);
        Assert.AreSame(responses[1], Assert.ContainsSingle(calls).Result);

        // The retry disposes the first result, and the fallback disposes the last one.
        Assert.IsTrue(responses[0].Disposed);
        Assert.IsTrue(responses[1].Disposed);
    }

    // §6.9
    [TestMethod]
    public async Task TheFallbackReceivesARetryTimeoutExceptionFromTheTotalTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        var calls = new List<FallbackArgs<string>>();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            }).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(totalTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(totalTimeout, timeout.Timeout);
    }

    [TestMethod]
    public async Task TheFallbackReceivesARetryTimeoutExceptionFromTheAttemptTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = attemptTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(attemptTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(attemptTimeout, timeout.Timeout);
    }

    [TestMethod]
    public async Task AnOpenCircuitGivesTheFallbackValue()
    {
        RecordingFakeTimeProvider fake = new();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, FailureRatio = 1, TimeProvider = fake });
        var opener = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        var calls = new List<FallbackArgs<string>>();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        });

        string result = await pipeline.ExecuteAsync(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return new ValueTask<string>("success");
            }).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(0, invocations);
        Assert.IsInstanceOfType<BrokenCircuitException>(Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task AnIsolatedCircuitGivesTheFallbackValue()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions());
        breaker.Isolate();
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            Fallback = RecordingFallback(calls),
        });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.IsInstanceOfType<IsolatedCircuitException>(Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_ThrowsTheOperationCanceledExceptionAndDoesNotCallTheFallback()
    {
        using CancellationTokenSource cts = new();
        var calls = new List<FallbackArgs<string>>();
        int actions = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(calls) with
            {
                Action = _ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<string>(FallbackValue);
                },
            },
        });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.IsEmpty(calls);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public async Task ACancellationByTheCallerDuringTheTotalTimeout_DoesNotCallTheFallback()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cts = new();
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            TotalTimeout = TimeSpan.FromMilliseconds(100),
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task ATokenThatIsAlreadyCancelled_ThrowsTheOperationCanceledExceptionAndDoesNotCallTheFallback()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync().ConfigureAwait(false);
        var calls = new List<FallbackArgs<string>>();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string> { Fallback = RecordingFallback(calls) });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return new ValueTask<string>("success");
                },
                cts.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(0, invocations);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task AnOperationCanceledExceptionThatTheCallerDidNotCause_GivesTheFallbackValue()
    {
        var cancellation = new OperationCanceledException("Not caused by the caller");
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls),
        });

        string result = await pipeline.ExecuteAsync(_ => throw cancellation).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreSame(cancellation, Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task AnExceptionThatThePolicyRefuses_Propagates()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(false);
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls) with { Policy = policy },
        });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreSame(thrown, Assert.ContainsSingle(policy.Consulted));
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task AnExceptionFromThePolicy_PropagatesAndTheFallbackIsNotCalled()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policyException = new FormatException("The policy failed.");
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls) with { Policy = RetryPolicies.FromPredicate(_ => throw policyException) },
        });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(policyException, thrown);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task TheFallbackPolicyIsIndependentOfTheRetryPolicy()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => new FormatException($"Attempt {attempt} failed"));
        var calls = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Policy = RetryPolicies.Handle<InvalidOperationException>(),
            Fallback = RecordingFallback(calls) with { Policy = RetryPolicies.Handle<FormatException>() },
        });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        // The retry policy refuses the exception, so there is one attempt. The fallback policy accepts it.
        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.LastThrown, Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task AnExceptionFromTheAction_Propagates()
    {
        var actionFailure = new FormatException("The action failed");
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new FallbackOptions<string> { Action = _ => throw actionFailure },
        });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);

        Assert.AreSame(actionFailure, thrown);
    }

    [TestMethod]
    public async Task AnExceptionFromOnFallback_PropagatesAndTheActionIsNotCalled()
    {
        var callbackFailure = new FormatException("The callback failed");
        int actions = 0;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new FallbackOptions<string>
            {
                OnFallback = _ => throw callbackFailure,
                Action = _ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<string>(FallbackValue);
                },
            },
        });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public async Task OnFallbackIsAwaitedBeforeTheAction()
    {
        var onFallbackCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new FallbackOptions<string>
            {
                OnFallback = _ =>
                {
                    lock (log)
                    {
                        log.Add("on fallback");
                    }

                    return new ValueTask(onFallbackCompletion.Task);
                },
                Action = _ =>
                {
                    lock (log)
                    {
                        log.Add("action");
                    }

                    return new ValueTask<string>(FallbackValue);
                },
            },
        });

        Task<string> call = pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Failure")).AsTask();

        Assert.IsFalse(call.IsCompleted);
        lock (log)
        {
            Assert.AreSequenceEqual(new[] { "on fallback" }, log);
        }

        onFallbackCompletion.SetResult();

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { "on fallback", "action" }, log);
    }

    [TestMethod]
    public async Task OnFallbackAndTheActionReceiveTheCallersToken()
    {
        using CancellationTokenSource cts = new();
        var calls = new List<FallbackArgs<string>>();
        CancellationToken actionToken = default;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromHours(1),
            TotalTimeout = TimeSpan.FromHours(1),
            Fallback = RecordingFallback(calls) with
            {
                Action = args =>
                {
                    actionToken = args.CancellationToken;
                    return new ValueTask<string>(FallbackValue);
                },
            },
        });

        string result = await pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Failure"), cts.Token).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(cts.Token, Assert.ContainsSingle(calls).CancellationToken);
        Assert.AreEqual(cts.Token, actionToken);
    }

    [TestMethod]
    public async Task WhenFallbackIsNull_ThePipelineThrowsTheException()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string> { Strategy = new Count(2), Fallback = null });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public void ThePolicyOfTheFallbackIsAnyExceptionPolicyByDefault()
    {
        var fallback = new FallbackOptions<string> { Action = _ => new ValueTask<string>(FallbackValue) };

        Assert.AreSame(AnyExceptionPolicy.Default, fallback.Policy);
        Assert.IsNull(fallback.ShouldFallbackResult);
        Assert.IsNull(fallback.OnFallback);
    }

    [TestMethod]
    public void TheConstructorRejectsAFallbackWithANullAction()
    {
        var options = new ReliabilityPipelineOptions<string> { Fallback = new FallbackOptions<string> { Action = null! } };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new ReliabilityPipeline<string>(options));

        Assert.AreEqual("Fallback", thrown.ParamName);
    }

    [TestMethod]
    public void TheConstructorRejectsAFallbackWithANullPolicy()
    {
        var options = new ReliabilityPipelineOptions<string>
        {
            Fallback = new FallbackOptions<string> { Action = _ => new ValueTask<string>(FallbackValue), Policy = null! },
        };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new ReliabilityPipeline<string>(options));

        Assert.AreEqual("Fallback", thrown.ParamName);
    }

    private static FallbackOptions<string> RecordingFallback(List<FallbackArgs<string>> calls)
    {
        return RecordingFallback(calls, FallbackValue);
    }

    // A fallback that records each call to OnFallback and returns the value.
    private static FallbackOptions<T> RecordingFallback<T>(List<FallbackArgs<T>> calls, T value)
    {
        return new FallbackOptions<T>
        {
            OnFallback = args =>
            {
                lock (calls)
                {
                    calls.Add(args);
                }

                return ValueTask.CompletedTask;
            },
            Action = _ => new ValueTask<T>(value),
        };
    }

    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }

    // A disposable result with a status code. It writes its disposal to the log.
    private sealed class Response(int statusCode, List<string> log) : IDisposable
    {
        public int StatusCode => statusCode;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            this.Disposed = true;
            log.Add($"dispose {statusCode}");
        }
    }
}
