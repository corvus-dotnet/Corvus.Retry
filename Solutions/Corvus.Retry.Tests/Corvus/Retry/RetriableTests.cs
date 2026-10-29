// <copyright file="RetriableTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Behaviour of the <see cref="Retriable"/> overloads that take a cancellation token, strategy and policy.
/// </summary>
/// <remarks>
/// Only zero-delay strategies are used here, so these tests never wait for a delay and can run in parallel.
/// Delays are covered by <see cref="RetriableDelayTests"/>.
/// </remarks>
[TestClass]
public class RetriableTests
{
    public static IEnumerable<object[]> NullArgumentCases
    {
        get
        {
            foreach (MethodInfo method in PublicRetryMethods())
            {
                foreach (ParameterInfo parameter in method.GetParameters().Where(IsNullableArgument))
                {
                    yield return [Describe(method), parameter.Name!];
                }
            }
        }
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheOperationFailsTwiceThenSucceeds_ItIsInvokedThreeTimesAndTheThirdResultIsReturned(RetryShape shape)
    {
        var op = new FlakyOperation(2);

        string? result = await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        if (shape is RetryShape.Function or RetryShape.AsyncFunction or RetryShape.AsyncFunctionWithToken)
        {
            Assert.AreEqual("attempt-3", result);
        }
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenEveryTryFails_TheLastExceptionIsRethrownAfterMaxTriesInvocations(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var strategy = new Count(3);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, strategy, new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        Assert.AreSame(op.Thrown[2], thrown);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenThePolicyRejectsTheException_TheOperationIsNotRetriedAndTheExceptionIsRethrown(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(false);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), policy)).ConfigureAwait(false);

        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.Thrown[0], thrown);

        // The policy sees the operation's own exception, not a wrapper.
        Assert.AreSame(op.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task ThePolicyIsConsultedForEveryFailure_RetryingOnlyWhileItSaysYes(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        int consultations = 0;
        var policy = new RecordingRetryPolicy(_ => Interlocked.Increment(ref consultations) == 1);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), policy)).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
        Assert.AreSame(op.Thrown[1], thrown);
        Assert.HasCount(2, policy.Consulted);
        Assert.AreSame(op.Thrown[0], policy.Consulted[0]);
        Assert.AreSame(op.Thrown[1], policy.Consulted[1]);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenThePolicyAcceptsOnlySomeExceptions_AnotherExceptionStopsTheRetry(RetryShape shape)
    {
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            attempt => attempt < 3 ? new TimeoutException($"Attempt {attempt} timed out") : new InvalidOperationException($"Attempt {attempt} failed"));
        var policy = new RecordingRetryPolicy(ex => ex is TimeoutException);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(10), policy)).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        Assert.AreSame(op.Thrown[2], thrown);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheStrategyIsDoNotRetry_TheOperationRunsOnceAndItsExceptionIsRethrownEvenThoughThePolicyAllowsRetry(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new DoNotRetry(), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.Thrown[0], thrown);
    }

    [TestMethod]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheStrategyIsDoNotRetryAndTheOperationSucceeds_TheResultIsReturned(RetryShape shape)
    {
        var op = new FlakyOperation(0);

        string? result = await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new DoNotRetry(), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual("attempt-1", result);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheOperationSucceedsFirstTime_ThePolicyIsNotConsulted(RetryShape shape)
    {
        var op = new FlakyOperation(0);
        var policy = new RecordingRetryPolicy(true);

        await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), policy).ConfigureAwait(false);

        Assert.AreEqual(1, op.Invocations);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheTokenIsAlreadyCancelled_AnOperationThatWouldFailIsNotInvokedAndTheCallIsCancelled(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        // The token is checked before each attempt, including the first.
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(5), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(0, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheTokenIsAlreadyCancelled_AnOperationThatWouldSucceedIsNotInvokedAndTheCallIsCancelled(RetryShape shape)
    {
        var op = new FlakyOperation(0);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(5), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(0, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenCancellationIsRequestedDuringRetries_NoFurtherRetriesHappenAndTheCallIsCancelled(RetryShape shape)
    {
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            attempt =>
            {
                if (attempt == 2)
                {
                    cts.Cancel();
                }

                return new InvalidOperationException($"Attempt {attempt} failed");
            });

        // The strategy and the policy accept the second failure, but the token is checked before the retry.
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(10), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheOperationThrowsACancellationThatTheTokenCaused_ItIsNotRetriedEvenThoughThePolicyAllowsIt(RetryShape shape)
    {
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });
        var policy = new RecordingRetryPolicy(true);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(10), policy)).ConfigureAwait(false);

        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.Thrown[0], thrown);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheOperationThrowsACancellationThatTheTokenDidNotCause_ThePolicyDecides(RetryShape shape)
    {
        var op = new FlakyOperation(1, _ => new OperationCanceledException("The operation cancelled itself"));
        var policy = new RecordingRetryPolicy(true);

        await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(10), policy).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
        Assert.AreSame(op.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task TheTokenPassedToTheOperationIsCancelledWhenTheCallerCancels(RetryShape shape)
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> WaitForCancellationAsync(CancellationToken token)
        {
            started.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "never";
        }

        Task call = shape == RetryShape.AsyncActionWithToken
            ? Retriable.RetryAsync(token => (Task)WaitForCancellationAsync(token), cts.Token, new Count(5), new AnyExceptionPolicy())
            : Retriable.RetryAsync(WaitForCancellationAsync, cts.Token, new Count(5), new AnyExceptionPolicy());

        CancellationToken received = await started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsFalse(received.IsCancellationRequested);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        Assert.IsTrue(received.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task TheOnRetryCallbackIsCalledBeforeEachRetryWithThatFailureTheDelayAndTheAttemptNumber(RetryShape shape)
    {
        var op = new FlakyOperation(2);
        var calls = new List<(RetryEventArgs Args, int InvocationsSoFar)>();
        ValueTask OnRetry(RetryEventArgs args)
        {
            lock (calls)
            {
                calls.Add((args, op.Invocations));
            }

            return ValueTask.CompletedTask;
        }

        await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy(), OnRetry).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        Assert.HasCount(2, calls);
        for (int i = 0; i < calls.Count; i++)
        {
            (RetryEventArgs args, int invocationsSoFar) = calls[i];
            Assert.AreSame(op.Thrown[i], args.LastException);
            Assert.IsNull(args.Result);
            Assert.AreEqual(TimeSpan.Zero, args.DelayBeforeRetry);
            Assert.AreEqual(i + 1, args.AttemptNumber);
            Assert.AreEqual(i + 2, args.NextAttemptNumber);

            // The callback runs after the attempt that failed and before the next one.
            Assert.AreEqual(i + 1, invocationsSoFar);
        }
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task TheOnRetryCallbackIsNotCalledForTheFinalFailure(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        int callbacks = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(
                shape,
                op.Invoke,
                CancellationToken.None,
                new Count(3),
                new AnyExceptionPolicy(),
                _ =>
                {
                    Interlocked.Increment(ref callbacks);
                    return ValueTask.CompletedTask;
                })).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        Assert.AreEqual(2, callbacks);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task AnExceptionFromTheOnRetryCallbackStopsTheCallAndIsThrownToTheCaller(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var callbackFailure = new FormatException("The callback failed");

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy(), _ => throw callbackFailure)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenAStrategyInstanceIsReused_EachCallGetsAllItsTries(RetryShape shape)
    {
        var strategy = new Count(3);
        var first = new FlakyOperation(2);
        await RetriableDriver.RetryAsync(shape, first.Invoke, CancellationToken.None, strategy, new AnyExceptionPolicy()).ConfigureAwait(false);

        // The strategy holds no state for a call, so the tries of the first call do not reduce those of the second.
        var second = new FlakyOperation(FlakyOperation.AlwaysFails);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, second.Invoke, CancellationToken.None, strategy, new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(3, first.Invocations);
        Assert.AreEqual(3, second.Invocations);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "continueOnCapturedContext: true posts continuations to the caller's context")]
    [DataRow(false, DisplayName = "continueOnCapturedContext: false never posts to the caller's context")]
    public async Task WhenContinueOnCapturedContextIsTrue_ContinuationsArePostedToTheCallersSynchronizationContext(bool continueOnCapturedContext)
    {
        var op = new FlakyOperation(1);
        var context = new CountingSynchronizationContext();
        Func<Task<string>> asyncFunc = async () =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            return op.Invoke();
        };

        Task<string> retry;
        SynchronizationContext? original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            retry = Retriable.RetryAsync(asyncFunc, CancellationToken.None, new Count(2), new AnyExceptionPolicy(), continueOnCapturedContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        string result = await retry.ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        if (continueOnCapturedContext)
        {
            Assert.IsGreaterThan(0, context.Posts);
        }
        else
        {
            Assert.AreEqual(0, context.Posts);
        }
    }

    [TestMethod]
    [DynamicData(nameof(NullArgumentCases))]
    public void EveryOverloadRejectsANullOperationStrategyOrPolicyWhenCalled(string overload, string parameterName)
    {
        MethodInfo method = PublicRetryMethods().Single(m => Describe(m) == overload);
        object?[] arguments = method.GetParameters()
            .Select(p => p.Name == parameterName ? null : p.HasDefaultValue ? p.DefaultValue : ValidArgument(p.ParameterType))
            .ToArray();

        // The async overloads throw before returning a task, rather than returning a faulted one.
        TargetInvocationException thrown = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, arguments));
        ArgumentNullException ex = Assert.IsInstanceOfType<ArgumentNullException>(thrown.InnerException);

        Assert.AreEqual(parameterName, ex.ParamName);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenRetriesAreExhausted_TheRethrownExceptionKeepsTheStackTraceOfTheMethodThatThrewIt(RetryShape shape)
    {
        Exception thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, ThrowFromADistinctiveMethod, CancellationToken.None, new Count(2), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.Contains(nameof(ThrowFromADistinctiveMethod), thrown.StackTrace!);
    }

    [TestMethod]
    public async Task WhenTheOperationReturnsANullTask_RetryAsyncThrowsInvalidOperationException()
    {
        int invocations = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Retriable.RetryAsync(
                () =>
                {
                    invocations++;
                    return (Task)null!;
                },
                CancellationToken.None,
                new DoNotRetry(),
                new AnyExceptionPolicy())).ConfigureAwait(false);
        Assert.AreEqual(1, invocations);

        invocations = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Retriable.RetryAsync(
                () =>
                {
                    invocations++;
                    return (Task<string>)null!;
                },
                CancellationToken.None,
                new DoNotRetry(),
                new AnyExceptionPolicy())).ConfigureAwait(false);
        Assert.AreEqual(1, invocations);
    }

    // Not inlined, so that its frame is in the stack trace of the exception it throws.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ThrowFromADistinctiveMethod() => throw new InvalidOperationException("Always fails");

    private static IEnumerable<MethodInfo> PublicRetryMethods()
    {
        return typeof(Retriable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is nameof(Retriable.Retry) or nameof(Retriable.RetryAsync))

            // The obsolete overloads only stop an asynchronous operation from binding to Retry. No call can reach them.
            .Where(m => m.GetCustomAttribute<ObsoleteAttribute>() is null)
            .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(object)) : m);
    }

    // An optional parameter, such as onRetry, accepts null.
    private static bool IsNullableArgument(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        return !parameter.HasDefaultValue && (typeof(Delegate).IsAssignableFrom(type) || type == typeof(IRetryStrategy) || type == typeof(IRetryPolicy));
    }

    private static string Describe(MethodInfo method)
    {
        return $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => FriendlyName(p.ParameterType)))})";
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static object ValidArgument(Type type)
    {
        if (type == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (type == typeof(IRetryStrategy))
        {
            return new Count();
        }

        if (type == typeof(IRetryPolicy))
        {
            return new AnyExceptionPolicy();
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(Action))
        {
            return new Action(() => { });
        }

        if (type == typeof(Func<object>))
        {
            return new Func<object>(() => new object());
        }

        if (type == typeof(Func<Task>))
        {
            return new Func<Task>(() => Task.CompletedTask);
        }

        if (type == typeof(Func<Task<object>>))
        {
            return new Func<Task<object>>(() => Task.FromResult(new object()));
        }

        if (type == typeof(Func<CancellationToken, Task>))
        {
            return new Func<CancellationToken, Task>(_ => Task.CompletedTask);
        }

        if (type == typeof(Func<CancellationToken, Task<object>>))
        {
            return new Func<CancellationToken, Task<object>>(_ => Task.FromResult(new object()));
        }

        throw new NotSupportedException($"No valid argument defined for {type}.");
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int posts;

        public int Posts => Volatile.Read(ref this.posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref this.posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
