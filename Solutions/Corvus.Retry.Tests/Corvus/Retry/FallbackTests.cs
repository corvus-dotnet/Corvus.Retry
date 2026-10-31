// <copyright file="FallbackTests.cs" company="Endjin Limited">
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
/// The <see cref="Fallback{T}"/> of a <see cref="RetryTaskFactory{T}"/> or a <see cref="HedgedTaskFactory{T}"/> is the
/// outermost part of a call: it gives a value for an exception that its policy accepts (including a
/// <see cref="RetryTimeoutException"/> from the total timeout and a <see cref="CircuitRejectedException"/>) and for a
/// result that <see cref="Fallback{T}.AppliesToResult"/> rejects, it awaits <see cref="Fallback{T}.OnFallback"/> before
/// <see cref="Fallback{T}.Action"/>, and it never applies when the caller cancels.
/// </summary>
[TestClass]
public class FallbackTests
{
    private const string FallbackValue = "fallback";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task AfterTheLastRetryFails_TheFactoryReturnsTheFallbackValue()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(calls),
        };

        string result = await factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(3, op.Invocations);
        FallbackEventArgs<string> args = Assert.ContainsSingle(calls);
        Assert.AreSame(op.LastThrown, args.Exception);
        Assert.IsNull(args.Result);
    }

    [TestMethod]
    public async Task WhenTheOperationSucceeds_TheFallbackDoesNotApply()
    {
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(calls),
        };

        ValueTask<string> call = factory.RunValueTask(_ => new ValueTask<string>("success"));

        // An operation that completes synchronously gives a call that completes synchronously.
        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("success", await call.ConfigureAwait(false));
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task ARejectedResult_GivesTheFallbackValueAndIsDisposedAfterOnFallbackAndBeforeAction()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        FallbackEventArgs<Response>? received = null;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = new Fallback<Response>(args =>
            {
                log.Add($"action, disposed: {args.Result!.Disposed}");
                return new ValueTask<Response>(new Response(200, log));
            })
            {
                AppliesToResult = r => r.StatusCode >= 500,
                OnFallback = args =>
                {
                    received = args;
                    log.Add($"on fallback, disposed: {args.Result!.Disposed}");
                    return ValueTask.CompletedTask;
                },
            },
        };

        Response result = await factory.RunValueTask(_ => new ValueTask<Response>(rejected)).ConfigureAwait(false);

        // OnFallback can examine the rejected result, as OnRetry can. The action gets a result that is already disposed.
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsFalse(result.Disposed);
        Assert.AreSequenceEqual(new[] { "on fallback, disposed: False", "dispose 503", "action, disposed: True" }, log);
        Assert.IsNotNull(received);
        Assert.AreSame(rejected, received.Value.Result);
        Assert.IsNull(received.Value.Exception);
    }

    [TestMethod]
    public async Task AResultThatAppliesToResultAccepts_IsReturnedAndNotDisposed()
    {
        var log = new List<string>();
        var ok = new Response(200, log);
        var calls = new List<FallbackEventArgs<Response>>();
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, new Response(0, log), appliesToResult: r => r.StatusCode >= 500),
        };

        Response result = await factory.RunValueTask(_ => new ValueTask<Response>(ok)).ConfigureAwait(false);

        Assert.AreSame(ok, result);
        Assert.IsFalse(ok.Disposed);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenAppliesToResultIsNull_NoResultGetsTheFallbackValue()
    {
        var log = new List<string>();
        var serverError = new Response(503, log);
        var calls = new List<FallbackEventArgs<Response>>();
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, new Response(200, log)),
        };

        Response result = await factory.RunValueTask(_ => new ValueTask<Response>(serverError)).ConfigureAwait(false);

        Assert.AreSame(serverError, result);
        Assert.IsFalse(serverError.Disposed);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenNoRetriesRemainForARejectedResult_TheFallbackReceivesTheLastResult()
    {
        var log = new List<string>();
        var responses = new List<Response>();
        var calls = new List<FallbackEventArgs<Response>>();
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new Count(2),
            CanRetryResult = r => r.StatusCode >= 500,
            Fallback = RecordingFallback(calls, new Response(200, log), appliesToResult: r => r.StatusCode >= 500),
        };

        Response result = await factory.RunValueTask(
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WhenAppliesToResultThrows_TheCallerGetsThatExceptionAndTheResultIsDisposed(bool operationCompletesAsynchronously)
    {
        var log = new List<string>();
        var result = new Response(503, log);
        var failure = new FormatException("AppliesToResult failed.");
        var calls = new List<FallbackEventArgs<Response>>();
        int actions = 0;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(
                calls,
                new Response(200, log),
                appliesToResult: _ => throw failure,
                action: _ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<Response>(new Response(200, log));
                }),
        };

        async ValueTask<Response> OperationAsync(CancellationToken cancellationToken)
        {
            if (operationCompletesAsynchronously)
            {
                await Task.Yield();
            }

            return result;
        }

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(OperationAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(failure, thrown);
        Assert.IsTrue(result.Disposed);
        Assert.IsEmpty(calls);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public async Task WhenAppliesToResultThrowsAndTheDisposalOfTheResultThrows_TheCallerGetsThePredicatesException()
    {
        var log = new List<string>();
        var result = new Response(503, log) { DisposeFailure = new InvalidOperationException("Dispose failed.") };
        var failure = new FormatException("AppliesToResult failed.");
        var calls = new List<FallbackEventArgs<Response>>();
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, new Response(200, log), appliesToResult: _ => throw failure),
        };

        // The predicate's exception is the outcome of the call. The factory ignores the exception from the disposal.
        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(_ => new ValueTask<Response>(result)).AsTask()).ConfigureAwait(false);

        Assert.AreSame(failure, thrown);
        Assert.IsTrue(result.Disposed);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenTheDisposalOfARejectedResultThrows_TheCallerGetsThatExceptionAfterOnFallbackAndTheActionDoesNotRun()
    {
        var log = new List<string>();
        var disposeFailure = new InvalidOperationException("Dispose failed.");
        var result = new Response(503, log) { DisposeFailure = disposeFailure };
        var calls = new List<FallbackEventArgs<Response>>();
        int actions = 0;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(
                calls,
                new Response(200, log),
                appliesToResult: r => r.StatusCode >= 500,
                action: _ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<Response>(new Response(200, log));
                }),
        };

        // The factory disposes the rejected result after OnFallback and before the action. An exception from the
        // disposal stops the call.
        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => factory.RunValueTask(_ => new ValueTask<Response>(result)).AsTask()).ConfigureAwait(false);

        Assert.AreSame(disposeFailure, thrown);
        Assert.AreSequenceEqual(new[] { "dispose 503" }, log);
        Assert.AreSame(result, Assert.ContainsSingle(calls).Result);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WhenOnFallbackThrowsForARejectedResult_TheResultIsDisposedAndTheCallerGetsTheCallbacksException(bool disposalThrows)
    {
        var log = new List<string>();
        var callbackFailure = new FormatException("OnFallback failed.");
        var rejected = new Response(503, log) { DisposeFailure = disposalThrows ? new InvalidOperationException("Dispose failed.") : null };
        int actions = 0;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            Fallback = new Fallback<Response>(_ =>
            {
                Interlocked.Increment(ref actions);
                return new ValueTask<Response>(new Response(200, log));
            })
            {
                AppliesToResult = r => r.StatusCode >= 500,
                OnFallback = _ => throw callbackFailure,
            },
        };

        // The callback's exception stops the call. The caller does not get the result, so the factory disposes it, and
        // an exception from that disposal does not hide the callback's exception.
        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(_ => new ValueTask<Response>(rejected)).AsTask()).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.IsTrue(rejected.Disposed);
        Assert.AreSequenceEqual(new[] { "dispose 503" }, log);
        Assert.AreEqual(0, actions);
    }

    // §6.9
    [TestMethod]
    public async Task TheFallbackReceivesARetryTimeoutExceptionFromTheTotalTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        var calls = new List<FallbackEventArgs<string>>();
        var tokens = new List<CancellationToken>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        Task<string> call = factory.RunValueTask(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            }).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(totalTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        CancellationToken token = Assert.ContainsSingle(tokens);
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(typeof(RetryTimeoutException), timeout.GetType());
        Assert.AreEqual(totalTimeout, timeout.Timeout);

        // The inner exception is the cancellation of the attempt, by the token that the operation received.
        Assert.AreEqual(token, Assert.IsInstanceOfType<OperationCanceledException>(timeout.InnerException).CancellationToken);
    }

    [TestMethod]
    public async Task TheFallbackReceivesARetryTimeoutExceptionFromTheAttemptTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var calls = new List<FallbackEventArgs<string>>();
        var tokens = new List<CancellationToken>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = attemptTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        Task<string> call = factory.RunValueTask(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            }).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(attemptTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        CancellationToken token = Assert.ContainsSingle(tokens);
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(attemptTimeout, timeout.Timeout);
        Assert.AreEqual(token, Assert.IsInstanceOfType<OperationCanceledException>(timeout.InnerException).CancellationToken);
    }

    [TestMethod]
    public async Task AnOpenCircuitGivesTheFallbackValue()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan breakDuration = TimeSpan.FromSeconds(30);
        var breaker = new CircuitBreaker(failureRate: 1, minimumCalls: 2, openDuration: breakDuration, timeProvider: fake);
        var opener = new RetryTaskFactory { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = fake };
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.RunValueTask<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        var calls = new List<FallbackEventArgs<string>>();
        int invocations = 0;
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        string result = await factory.RunValueTask(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return new ValueTask<string>("success");
            }).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(0, invocations);
        Exception received = Assert.ContainsSingle(calls).Exception!;
        Assert.AreEqual(typeof(CircuitRejectedException), received.GetType());

        // The breaker of the options made the exception: no time has passed, so the whole break remains.
        Assert.AreEqual(breakDuration, ((CircuitRejectedException)received).RetryAfter);
    }

    [TestMethod]
    public async Task AnIsolatedCircuitGivesTheFallbackValue()
    {
        var breaker = new CircuitBreaker();
        breaker.Isolate();
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            Fallback = RecordingFallback(calls),
        };

        string result = await factory.RunValueTask(_ => new ValueTask<string>("success")).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Exception received = Assert.ContainsSingle(calls).Exception!;
        Assert.AreEqual(typeof(CircuitIsolatedException), received.GetType());
        Assert.IsNull(((CircuitIsolatedException)received).RetryAfter);
        Assert.AreEqual(CircuitState.Isolated, breaker.State);
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_ThrowsTheOperationCanceledExceptionAndDoesNotCallTheFallback()
    {
        using CancellationTokenSource cts = new();
        var calls = new List<FallbackEventArgs<string>>();
        int actions = 0;
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Fallback = RecordingFallback(
                calls,
                action: _ =>
                {
                    Interlocked.Increment(ref actions);
                    return new ValueTask<string>(FallbackValue);
                }),
        };

        Task<string> call = factory.RunValueTask(NeverCompletesAsync, cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);

        // Without timeouts, the operation receives the caller's token, and its cancellation propagates.
        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsEmpty(calls);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public async Task ACancellationByTheCallerDuringTheTotalTimeout_DoesNotCallTheFallback()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cts = new();
        var calls = new List<FallbackEventArgs<string>>();
        var tokens = new List<CancellationToken>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            TotalTimeout = TimeSpan.FromMilliseconds(100),
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        Task<string> call = factory.RunValueTask(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            },
            cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(100));

        // With a total timeout, the operation receives a token that the caller's token cancels. The caller sees an
        // exception that carries its own token, and the fallback does not apply.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreNotEqual(Assert.ContainsSingle(tokens), thrown.CancellationToken);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsAfterALastAttemptThatCancelledItself_TheFallbackDoesNotApply()
    {
        using CancellationTokenSource cts = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => new OperationCanceledException($"Attempt {attempt} cancelled itself"));
        var calls = new List<FallbackEventArgs<string>>();
        var stateChangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStateChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(2),

            // The failure of the second attempt opens the circuit, and the factory awaits the callback.
            CircuitBreaker = new CircuitBreaker(
                failureRate: 1,
                minimumCalls: 2,
                onStateChanged: change =>
                {
                    stateChangeStarted.SetResult();
                    return new ValueTask(releaseStateChange.Task);
                }),
            Fallback = RecordingFallback(calls),
        };

        Task<string> call = factory.RunValueTask(_ => new ValueTask<string>(op.Invoke()), cts.Token).AsTask();

        // The last attempt cancelled itself before the caller cancelled, so the engine makes that cancellation the
        // outcome of the attempts. The caller cancels while the factory awaits the callback.
        await stateChangeStarted.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        releaseStateChange.SetResult();

        // The fallback never applies once the caller has cancelled. The caller gets the outcome of the attempts.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(2, op.Invocations);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task ATokenThatIsAlreadyCancelled_ThrowsTheOperationCanceledExceptionAndDoesNotCallTheFallback()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync().ConfigureAwait(false);
        var calls = new List<FallbackEventArgs<string>>();
        int invocations = 0;
        var factory = new RetryTaskFactory<string> { Fallback = RecordingFallback(calls) };

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => factory.RunValueTask(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return new ValueTask<string>("success");
                },
                cts.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(0, invocations);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task AnOperationCanceledExceptionThatTheCallerDidNotCause_GivesTheFallbackValue()
    {
        var cancellation = new OperationCanceledException("Not caused by the caller");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls),
        };

        string result = await factory.RunValueTask(_ => throw cancellation).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreSame(cancellation, Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task AnExceptionThatThePolicyRefuses_Propagates()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(false);
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, policy: policy),
        };

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreSame(thrown, Assert.ContainsSingle(policy.Consulted));
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task TheFallbackPolicyIsAskedAboutTheExceptionsInsideAnAggregateException()
    {
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            attempt => new AggregateException(new HttpRequestException($"Attempt {attempt} failed")));
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(2),
            Fallback = RecordingFallback(calls, policy: RetryPolicies.For<HttpRequestException>()),
        };

        string result = await factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        // The policy accepts the exception that the operation threw inside the aggregate. The fallback receives the
        // aggregate itself.
        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(2, op.Invocations);
        Assert.AreSame(op.LastThrown, Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task WhenTheFallbackPolicyRefusesOneExceptionInsideAnAggregateException_TheAggregatePropagates()
    {
        var aggregate = new AggregateException(new HttpRequestException("Accepted"), new InvalidOperationException("Refused"));
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, policy: RetryPolicies.For<HttpRequestException>()),
        };

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => factory.RunValueTask(_ => throw aggregate).AsTask()).ConfigureAwait(false);

        Assert.AreSame(aggregate, thrown);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task AnExceptionFromThePolicy_PropagatesAndTheFallbackIsNotCalled()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policyException = new FormatException("The policy failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = RecordingFallback(calls, policy: RetryPolicies.FromPredicate(_ => throw policyException)),
        };

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(policyException, thrown);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task TheFallbackPolicyIsIndependentOfTheRetryPolicy()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => new FormatException($"Attempt {attempt} failed"));
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Policy = RetryPolicies.For<InvalidOperationException>(),
            Fallback = RecordingFallback(calls, policy: RetryPolicies.For<FormatException>()),
        };

        string result = await factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        // The retry policy refuses the exception, so there is one attempt. The fallback policy accepts it.
        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.LastThrown, Assert.ContainsSingle(calls).Exception);
    }

    [TestMethod]
    public async Task AnExceptionFromTheAction_Propagates()
    {
        var actionFailure = new FormatException("The action failed");
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new Fallback<string>(_ => throw actionFailure),
        };

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);

        Assert.AreSame(actionFailure, thrown);
    }

    [TestMethod]
    public async Task AnExceptionFromOnFallback_PropagatesAndTheActionIsNotCalled()
    {
        var callbackFailure = new FormatException("The callback failed");
        int actions = 0;
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new Fallback<string>(_ =>
            {
                Interlocked.Increment(ref actions);
                return new ValueTask<string>(FallbackValue);
            })
            {
                OnFallback = _ => throw callbackFailure,
            },
        };

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => factory.RunValueTask(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(0, actions);
    }

    [TestMethod]
    public async Task OnFallbackIsAwaitedBeforeTheAction()
    {
        var onFallbackCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            Fallback = new Fallback<string>(_ =>
            {
                lock (log)
                {
                    log.Add("action");
                }

                return new ValueTask<string>(FallbackValue);
            })
            {
                OnFallback = _ =>
                {
                    lock (log)
                    {
                        log.Add("on fallback");
                    }

                    return new ValueTask(onFallbackCompletion.Task);
                },
            },
        };

        Task<string> call = factory.RunValueTask(_ => throw new InvalidOperationException("Failure")).AsTask();

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
        var calls = new List<FallbackEventArgs<string>>();
        CancellationToken actionToken = default;
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromHours(1),
            TotalTimeout = TimeSpan.FromHours(1),
            TimeProvider = new RecordingFakeTimeProvider(),
            Fallback = RecordingFallback(
                calls,
                action: args =>
                {
                    actionToken = args.CancellationToken;
                    return new ValueTask<string>(FallbackValue);
                }),
        };

        string result = await factory.RunValueTask(_ => throw new InvalidOperationException("Failure"), cts.Token).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.AreEqual(cts.Token, Assert.ContainsSingle(calls).CancellationToken);
        Assert.AreEqual(cts.Token, actionToken);
    }

    [TestMethod]
    public async Task ForARejectedResult_OnFallbackAndTheActionReceiveTheCallersTokenAndTheResult()
    {
        using CancellationTokenSource cts = new();
        var log = new List<string>();
        var rejected = new Response(503, log);
        var onFallbackCalls = new List<FallbackEventArgs<Response>>();
        var actionCalls = new List<FallbackEventArgs<Response>>();
        CancellationToken operationToken = default;
        var factory = new RetryTaskFactory<Response>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromHours(1),
            TotalTimeout = TimeSpan.FromHours(1),
            TimeProvider = new RecordingFakeTimeProvider(),
            Fallback = RecordingFallback(
                onFallbackCalls,
                new Response(0, log),
                appliesToResult: r => r.StatusCode >= 500,
                action: args =>
                {
                    actionCalls.Add(args);
                    return new ValueTask<Response>(new Response(200, log));
                }),
        };

        Response result = await factory.RunValueTask(
            ct =>
            {
                operationToken = ct;
                return new ValueTask<Response>(rejected);
            },
            cts.Token).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);

        // The operation received a token of the timeouts, but the fallback receives the caller's token.
        Assert.AreNotEqual(cts.Token, operationToken);
        foreach (FallbackEventArgs<Response> args in new[] { Assert.ContainsSingle(onFallbackCalls), Assert.ContainsSingle(actionCalls) })
        {
            Assert.AreSame(rejected, args.Result);
            Assert.IsNull(args.Exception);
            Assert.AreEqual(cts.Token, args.CancellationToken);
        }
    }

    [TestMethod]
    public async Task WhenFallbackIsNull_TheFactoryThrowsTheException()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var factory = new RetryTaskFactory<string> { Strategy = new Count(2), Fallback = null };

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public void ThePolicyOfTheFallbackIsAnyExceptionPolicyByDefault()
    {
        var fallback = new Fallback<string>(_ => new ValueTask<string>(FallbackValue));

        Assert.AreSame(AnyExceptionPolicy.Default, fallback.Policy);
        Assert.IsNull(fallback.AppliesToResult);
        Assert.IsNull(fallback.OnFallback);
    }

    [TestMethod]
    public void TheConstructorOfTheFallbackRejectsANullAction()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new Fallback<string>(null!));

        Assert.AreEqual("action", thrown.ParamName);
    }

    [TestMethod]
    public void ThePolicyOfTheFallbackRejectsNull()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(
            () => new Fallback<string>(_ => new ValueTask<string>(FallbackValue)) { Policy = null! });

        Assert.AreEqual("Policy", thrown.ParamName);
    }

    [TestMethod]
    public void TheFallbackKeepsItsActionAndSettings()
    {
        Func<FallbackEventArgs<string>, ValueTask<string>> action = _ => new ValueTask<string>(FallbackValue);
        Func<string, bool> appliesToResult = r => r.Length == 0;
        Func<FallbackEventArgs<string>, ValueTask> onFallback = _ => ValueTask.CompletedTask;
        IRetryPolicy policy = RetryPolicies.For<TimeoutException>();

        var fallback = new Fallback<string>(action) { AppliesToResult = appliesToResult, OnFallback = onFallback, Policy = policy };

        Assert.AreSame(action, fallback.Action);
        Assert.AreSame(appliesToResult, fallback.AppliesToResult);
        Assert.AreSame(onFallback, fallback.OnFallback);
        Assert.AreSame(policy, fallback.Policy);
    }

    [TestMethod]
    public async Task AnExceptionFromOnRetry_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("OnRetry failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            OnRetry = _ => throw callbackException,
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromCanRetryResult_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("CanRetryResult failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            CanRetryResult = _ => throw callbackException,
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => new ValueTask<string>("success"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromTheStrategy_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("The strategy failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = RetryStrategies.FromDelegate(_ => throw callbackException),
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromTheRetryPolicy_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("The retry policy failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(3),
            Policy = RetryPolicies.FromPredicate(_ => throw callbackException),
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromThePolicyOfTheCircuitBreaker_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("The breaker policy failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = new CircuitBreaker(policy: RetryPolicies.FromPredicate(_ => throw callbackException)),
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromTheStateChangeCallbackOfTheCircuitBreaker_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("OnStateChanged failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            // The second failed attempt opens the circuit.
            Strategy = new Count(2),
            CircuitBreaker = new CircuitBreaker(
                failureRate: 1,
                minimumCalls: 2,
                onStateChanged: change => change.To == CircuitState.Open ? throw callbackException : ValueTask.CompletedTask),
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromTheStateChangeCallbackOnTheChangeFromOpenToHalfOpen_PropagatesAndTheFallbackIsNotCalled()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan breakDuration = TimeSpan.FromSeconds(30);
        var callbackException = new FormatException("OnStateChanged failed.");
        var calls = new List<FallbackEventArgs<string>>();
        int invocations = 0;
        var breaker = new CircuitBreaker(
            failureRate: 1,
            minimumCalls: 2,
            openDuration: breakDuration,
            onStateChanged: change => change.To == CircuitState.HalfOpen ? throw callbackException : ValueTask.CompletedTask,
            timeProvider: fake);
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        // Two failed calls open the circuit. The fallback gives a value for each.
        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual(FallbackValue, await factory.RunValueTask(_ => throw new InvalidOperationException("Failure")).ConfigureAwait(false));
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        fake.Advance(breakDuration);

        Exception thrown = await Assert.ThrowsAsync<Exception>(
            () => factory.RunValueTask(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return new ValueTask<string>("success");
                }).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The callback's exception stops the call before the attempt, and the fallback does not apply to it.
        Assert.AreSame(callbackException, thrown);
        Assert.AreEqual(0, invocations);
        Assert.HasCount(2, calls);
    }

    [TestMethod]
    public async Task AnExceptionFromOnHedging_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("OnHedgedAttempt failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new HedgedTaskFactory<string>(1, TimeSpan.Zero)
        {
            OnHedgedAttempt = _ => throw callbackException,
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromTheHedgingPolicy_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("The hedging policy failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new HedgedTaskFactory<string>(1, TimeSpan.Zero)
        {
            Policy = RetryPolicies.FromPredicate(_ => throw callbackException),
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnExceptionFromOperationForAttempt_PropagatesAndTheFallbackIsNotCalled()
    {
        var callbackException = new FormatException("OperationForAttempt failed.");
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new HedgedTaskFactory<string>(1, TimeSpan.Zero)
        {
            OperationForAttempt = attempt => attempt == 1 ? null : throw callbackException,
            Fallback = RecordingFallback(calls),
        };

        await AssertCallbackExceptionPropagatesAsync(
            factory, _ => throw new InvalidOperationException("Failure"), callbackException, calls).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WithHedging_TheFallbackReceivesTheExceptionOfTheLastAttempt()
    {
        var calls = new List<FallbackEventArgs<string>>();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var factory = new HedgedTaskFactory<string>(1, TimeSpan.Zero)
        {
            Fallback = RecordingFallback(calls),
        };

        // The first attempt fails synchronously, so the engine starts the second at once. The second fails only after
        // the engine awaits it, so it is the last attempt to fail.
        ValueTask<string> OperationAsync(CancellationToken cancellationToken)
        {
            try
            {
                return new ValueTask<string>(op.Invoke());
            }
            catch (InvalidOperationException ex) when (op.Invocations == 2)
            {
                return DeferredValueTask.FromException<string>(ex);
            }
        }

        string result = await factory.Run(ct => OperationAsync(ct).AsTask()).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(FallbackValue, result);
        Assert.HasCount(2, op.Thrown);
        Assert.AreSame(op.Thrown[1], Assert.ContainsSingle(calls).Exception);
        Assert.AreNotSame(op.Thrown[0], op.Thrown[1]);
    }

    [TestMethod]
    public async Task AFallbackPolicyThatHandlesOnlyInvalidOperationException_LetsARetryTimeoutExceptionPropagate()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = attemptTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls, policy: RetryPolicies.For<InvalidOperationException>()),
        };

        Task<string> call = factory.RunValueTask(NeverCompletesAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(attemptTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task AFallbackPolicyThatHandlesOnlyInvalidOperationException_LetsACircuitRejectedExceptionPropagate()
    {
        var breaker = new CircuitBreaker(failureRate: 1, minimumCalls: 2);
        var calls = new List<FallbackEventArgs<string>>();
        int invocations = 0;
        var factory = new RetryTaskFactory<string>
        {
            Strategy = new DoNotRetry(),
            CircuitBreaker = breaker,
            Fallback = RecordingFallback(calls, policy: RetryPolicies.For<InvalidOperationException>()),
        };

        // Two calls fail, the policy of the fallback accepts each failure, and the failures open the circuit.
        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual(FallbackValue, await factory.RunValueTask(_ => throw new InvalidOperationException("Failure")).ConfigureAwait(false));
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);

        CircuitRejectedException thrown = await Assert.ThrowsExactlyAsync<CircuitRejectedException>(
            () => factory.RunValueTask(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return new ValueTask<string>("success");
                }).AsTask()).ConfigureAwait(false);

        Assert.IsNotNull(thrown.RetryAfter);
        Assert.AreEqual(0, invocations);
        Assert.HasCount(2, calls);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringTheRetryDelay_TheFallbackReceivesTheRetryTimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new RetryTaskFactory<string>
        {
            Strategy = RetryStrategies.FromDelegate(_ => TimeSpan.FromSeconds(10)),
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        Task<string> call = factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(totalTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(totalTimeout, timeout.Timeout);

        // The total timeout expired between two attempts, so the fallback sees the last attempt's failure as the cause.
        Assert.AreSame(op.LastThrown, timeout.InnerException);
    }

    [TestMethod]
    public async Task WithHedging_TheFallbackReceivesARetryTimeoutExceptionFromTheTotalTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var calls = new List<FallbackEventArgs<string>>();
        var factory = new HedgedTaskFactory<string>(1, TimeSpan.FromSeconds(10))
        {
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
            Fallback = RecordingFallback(calls),
        };

        Task<string> call = factory.Run(ct => NeverCompletesAsync(ct).AsTask());

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(totalTimeout);

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(calls).Exception);
        Assert.AreEqual(totalTimeout, timeout.Timeout);

        // No attempt failed before the total timeout expired, so there is no failure to report.
        Assert.IsNull(timeout.InnerException);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task WithContinueOnCapturedContext_TheGenericFactoryAndItsFallbackContinueOnTheCapturedContextOnlyWhenItIsTrue(
        bool continueOnCapturedContext,
        bool aRejectedResultCausesTheFallback)
    {
        var context = new ThreadPoolSynchronizationContext();
        var seen = new Dictionary<string, SynchronizationContext?>();
        var actionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;

        void Record(string name)
        {
            lock (seen)
            {
                seen.Add(name, SynchronizationContext.Current);
            }
        }

        var factory = new RetryTaskFactory<string>
        {
            Strategy = new Count(2),
            ContinueOnCapturedContext = continueOnCapturedContext,
            CanRetryResult = r => r == "rejected",
            OnRetry = _ =>
            {
                Record("OnRetry");
                return DeferredValueTask.Completed();
            },
            Fallback = new Fallback<string>(_ =>
            {
                Record("Action");
                return DeferredValueTask.FromResult(FallbackValue, actionGate.Task);
            })
            {
                AppliesToResult = r => r == "rejected",
                OnFallback = _ =>
                {
                    Record("OnFallback");
                    return DeferredValueTask.Completed();
                },
            },
        };

        // Each attempt completes on a thread with no context, after the factory awaits it.
        ValueTask<string> OperationAsync(CancellationToken cancellationToken)
        {
            Record($"attempt {Interlocked.Increment(ref attempts)}");
            return aRejectedResultCausesTheFallback
                ? DeferredValueTask.FromResult("rejected")
                : DeferredValueTask.FromException<string>(new InvalidOperationException("Failure"));
        }

        (Task<string> call, Task<SynchronizationContext?> completedOn) = context.Run(() =>
        {
            Task<string> call = factory.RunValueTask(OperationAsync).AsTask();
            Task<SynchronizationContext?> completedOn = call.ContinueWith(
                _ => SynchronizationContext.Current,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return (call, completedOn);
        });

        // The call cannot complete before the continuation that records its context is registered.
        actionGate.SetResult();

        Assert.AreEqual(FallbackValue, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        SynchronizationContext? expected = continueOnCapturedContext ? context : null;
        Assert.AreSame(context, seen["attempt 1"]);
        Assert.AreSame(expected, seen["OnRetry"]);
        Assert.AreSame(expected, seen["attempt 2"]);
        Assert.AreSame(expected, seen["OnFallback"]);
        Assert.AreSame(expected, seen["Action"]);
        Assert.AreSame(expected, await completedOn.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    private static async Task AssertCallbackExceptionPropagatesAsync(
        RetryTaskFactory<string> factory,
        Func<CancellationToken, ValueTask<string>> operation,
        Exception callbackException,
        List<FallbackEventArgs<string>> calls)
    {
        Exception thrown = await Assert.ThrowsAsync<Exception>(
            () => factory.RunValueTask(operation).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(callbackException, thrown);
        Assert.IsEmpty(calls);
    }

    private static async Task AssertCallbackExceptionPropagatesAsync(
        HedgedTaskFactory<string> factory,
        Func<CancellationToken, Task<string>> operation,
        Exception callbackException,
        List<FallbackEventArgs<string>> calls)
    {
        Exception thrown = await Assert.ThrowsAsync<Exception>(
            () => factory.Run(operation).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(callbackException, thrown);
        Assert.IsEmpty(calls);
    }

    private static Fallback<string> RecordingFallback(
        List<FallbackEventArgs<string>> calls,
        Func<string, bool>? appliesToResult = null,
        IRetryPolicy? policy = null,
        Func<FallbackEventArgs<string>, ValueTask<string>>? action = null)
    {
        return RecordingFallback(calls, FallbackValue, appliesToResult, policy, action);
    }

    // A fallback that records each call to OnFallback and returns the value, unless the test gives its own action.
    private static Fallback<T> RecordingFallback<T>(
        List<FallbackEventArgs<T>> calls,
        T value,
        Func<T, bool>? appliesToResult = null,
        IRetryPolicy? policy = null,
        Func<FallbackEventArgs<T>, ValueTask<T>>? action = null)
    {
        return new Fallback<T>(action ?? (_ => new ValueTask<T>(value)))
        {
            AppliesToResult = appliesToResult,
            Policy = policy ?? AnyExceptionPolicy.Default,
            OnFallback = args =>
            {
                lock (calls)
                {
                    calls.Add(args);
                }

                return ValueTask.CompletedTask;
            },
        };
    }

    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }

    // A disposable result with a status code. It writes its disposal to the log. When DisposeFailure is set, Dispose
    // throws it after it records the disposal.
    private sealed class Response(int statusCode, List<string> log) : IDisposable
    {
        public int StatusCode => statusCode;

        public bool Disposed { get; private set; }

        public Exception? DisposeFailure { get; init; }

        public void Dispose()
        {
            this.Disposed = true;
            lock (log)
            {
                log.Add($"dispose {statusCode}");
            }

            if (this.DisposeFailure is { } failure)
            {
                throw failure;
            }
        }
    }
}
