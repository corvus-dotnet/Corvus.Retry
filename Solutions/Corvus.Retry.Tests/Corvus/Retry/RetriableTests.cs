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
/// Only zero-delay strategies are used here, so these tests never reach the sleep service and can run in parallel.
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
                foreach (ParameterInfo parameter in method.GetParameters().Where(p => IsNullableArgument(p.ParameterType)))
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
    public async Task WhenTheOperationFailsTwiceThenSucceeds_ItIsInvokedThreeTimesAndTheThirdResultIsReturned(RetryShape shape)
    {
        var op = new FlakyOperation(2);

        string? result = await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        if (shape is RetryShape.Function or RetryShape.AsyncFunction)
        {
            Assert.AreEqual("attempt-3", result);
        }
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
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
    public async Task WhenTheTokenIsAlreadyCancelledAndTheOperationFails_ItIsNotRetriedAndItsOwnExceptionIsRethrown(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        // Cancellation is only checked after a failure, to decide whether to retry. It does not prevent the
        // first try, and the caller sees the operation's exception rather than an OperationCanceledException.
        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(5), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(1, op.Invocations);
        Assert.AreSame(op.Thrown[0], thrown);
    }

    [TestMethod]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncFunction)]
    public async Task WhenTheTokenIsAlreadyCancelledButTheOperationSucceeds_TheResultIsReturned(RetryShape shape)
    {
        var op = new FlakyOperation(0);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        string? result = await RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(5), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual("attempt-1", result);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    public async Task WhenCancellationIsRequestedDuringRetries_NoFurtherRetriesHappen(RetryShape shape)
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

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Count(10), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
        Assert.AreSame(op.Thrown[1], thrown);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
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
            .Select(p => p.Name == parameterName ? null : ValidArgument(p.ParameterType))
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
    public async Task WhenRetriesAreExhausted_TheRethrownExceptionKeepsTheStackTraceOfTheMethodThatThrewIt(RetryShape shape)
    {
        Exception thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, ThrowFromADistinctiveMethod, CancellationToken.None, new Count(2), new AnyExceptionPolicy())).ConfigureAwait(false);

        StringAssert.Contains(thrown.StackTrace, nameof(ThrowFromADistinctiveMethod));
    }

    // Not inlined, so that its frame is in the stack trace of the exception it throws.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ThrowFromADistinctiveMethod() => throw new InvalidOperationException("Always fails");

    private static IEnumerable<MethodInfo> PublicRetryMethods()
    {
        return typeof(Retriable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is nameof(Retriable.Retry) or nameof(Retriable.RetryAsync))
            .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(object)) : m);
    }

    private static bool IsNullableArgument(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || type == typeof(IRetryStrategy) || type == typeof(IRetryPolicy);
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
