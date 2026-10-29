// <copyright file="RetriableOverloadTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Each public <see cref="Retriable"/> overload passes on each of its arguments: its token, its
/// <c>continueOnCapturedContext</c>, and the default strategy when it takes none.
/// </summary>
/// <remarks>
/// The overload is the <c>[DynamicData]</c> dimension, found by reflection, so a new overload gets these tests. Each test
/// passes a value that a dropped or defaulted argument would change. The context tests install a fake time provider,
/// so these tests must not run in parallel.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class RetriableOverloadTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    public static IEnumerable<object[]> OverloadsWithAToken => Overloads(RetriableOverloads.Has<CancellationToken>);

    public static IEnumerable<object[]> OverloadsWithNoStrategy => Overloads(m => !RetriableOverloads.Has<IRetryStrategy>(m));

    public static IEnumerable<object[]> OverloadsWhoseOperationTakesAToken => Overloads(RetriableOverloads.OperationTakesToken);

    // An overload with a bool is tested with both values. An asynchronous overload without one must not continue on
    // the captured context.
    public static IEnumerable<object[]> AsynchronousOverloadsAndTheirContextSetting =>
        RetriableOverloads.All
            .Where(m => !RetriableOverloads.IsSynchronous(m))
            .SelectMany(m => RetriableOverloads.Has<bool>(m)
                ? new[] { new object[] { RetriableOverloads.Describe(m), true }, [RetriableOverloads.Describe(m), false] }
                : [[RetriableOverloads.Describe(m), false]]);

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public void TheDynamicDataCoversEveryOverload()
    {
        // Retriable has 21 overloads that a call can reach. A new overload must be added to the tests below.
        Assert.HasCount(21, RetriableOverloads.All.ToList());
        Assert.HasCount(15, OverloadsWithAToken.ToList());
        Assert.HasCount(11, OverloadsWithNoStrategy.ToList());
        Assert.HasCount(6, OverloadsWhoseOperationTakesAToken.ToList());
        Assert.HasCount(6, RetriableOverloads.All.Where(RetriableOverloads.Has<bool>).ToList());
    }

    // Retriable: "The overloads that take a cancellation token check it before each attempt ... the call throws an
    // OperationCanceledException".
    [TestMethod]
    [DynamicData(nameof(OverloadsWithAToken))]
    public async Task WhenTheTokenIsAlreadyCancelled_EveryTokenOverloadThrowsWithThatTokenAndDoesNotInvoke(string overload)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RetriableOverloads.InvokeAsync(RetriableOverloads.Find(overload), _ => Task.FromResult(op.Invoke()), cts.Token, new Count(5), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(0, op.Invocations);
    }

    // The migration guide: the default strategy is Count(10), with the AnyExceptionPolicy.
    [TestMethod]
    [DynamicData(nameof(OverloadsWithNoStrategy))]
    public async Task WhenNoStrategyIsGiven_TheOperationIsTriedTenTimes(string overload)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableOverloads.InvokeAsync(RetriableOverloads.Find(overload), _ => Task.FromResult(op.Invoke()))).ConfigureAwait(false);

        Assert.AreEqual(10, op.Invocations);
        Assert.AreSame(op.LastThrown, thrown);
    }

    // Retriable: the operation "receives a token that is cancelled when cancellationToken is cancelled".
    [TestMethod]
    [DynamicData(nameof(OverloadsWhoseOperationTakesAToken))]
    public async Task TheOperationReceivesATokenThatTheCallersTokenCancels(string overload)
    {
        MethodInfo method = RetriableOverloads.Find(overload);
        using var cts = new CancellationTokenSource();
        var received = new List<CancellationToken>();

        string? result = await RetriableOverloads.InvokeAsync(
            method,
            ct =>
            {
                received.Add(ct);
                return Task.FromResult("done");
            },
            cts.Token).ConfigureAwait(false);

        CancellationToken token = Assert.ContainsSingle(received);
        Assert.IsFalse(token.IsCancellationRequested);
        await cts.CancelAsync().ConfigureAwait(false);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(method.ReturnType == typeof(Task<string>) ? "done" : null, result);
    }

    // The engine awaits the attempt, the onRetry callback and the delay, and applies continueOnCapturedContext to each.
    // Each awaited task completes on a thread that has no context, so only the engine's await can return to the context.
    [TestMethod]
    [DynamicData(nameof(AsynchronousOverloadsAndTheirContextSetting))]
    public async Task TheCodeAfterEachAwaitRunsOnTheCapturedContextOnlyWhenContinueOnCapturedContextIsTrue(string overload, bool continueOnCapturedContext)
    {
        MethodInfo method = RetriableOverloads.Find(overload);
        bool hasOnRetry = RetriableOverloads.Has<Func<RetryEventArgs, ValueTask>>(method);
        bool hasDelay = RetriableOverloads.Has<IRetryStrategy>(method);
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        var context = new ThreadPoolSynchronizationContext();

        // Completed by the test thread, so that a continuation that does not go to the context runs on that thread.
        var firstAttempt = new TaskCompletionSource<string>();
        var onRetryResult = new TaskCompletionSource();
        var onRetryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? inOnRetry = null;
        SynchronizationContext? atDelay = null;
        fake.OnTimerCreated = _ => atDelay = SynchronizationContext.Current;
        int attempts = 0;

        Task<string> Operation(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return firstAttempt.Task;
            }

            secondAttempt.SetResult(SynchronizationContext.Current);
            return Task.FromResult("attempt-2");
        }

        ValueTask OnRetry(RetryEventArgs args)
        {
            inOnRetry = SynchronizationContext.Current;
            onRetryStarted.SetResult();
            return new ValueTask(onRetryResult.Task);
        }

        Task call;
        SynchronizationContext? original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            call = (Task)RetriableOverloads.Invoke(method, Operation, CancellationToken.None, new Linear(OneHour, 2), new AnyExceptionPolicy(), continueOnCapturedContext, OnRetry)!;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        firstAttempt.SetException(new InvalidOperationException("The first attempt failed"));
        if (hasOnRetry)
        {
            await onRetryStarted.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
            onRetryResult.SetResult();
        }

        if (hasDelay)
        {
            await fake.WaitForTimersAsync(1).ConfigureAwait(false);
            fake.Advance(OneHour);
        }

        SynchronizationContext? inSecondAttempt = await secondAttempt.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        await call.WaitAsync(WaitLimit).ConfigureAwait(false);

        SynchronizationContext? expected = continueOnCapturedContext ? context : null;
        Assert.AreSame(expected, inSecondAttempt, "after the delay, or after the attempt when there is no delay");
        if (hasOnRetry)
        {
            Assert.AreSame(expected, inOnRetry, "after the attempt");
        }

        if (hasDelay)
        {
            Assert.AreSame(expected, atDelay, hasOnRetry ? "after the onRetry callback" : "after the attempt");
            Assert.AreEqual(OneHour, Assert.ContainsSingle(fake.Delays));
        }
    }

    private static IEnumerable<object[]> Overloads(Func<MethodInfo, bool> predicate)
    {
        return RetriableOverloads.All.Where(predicate).Select(m => new object[] { RetriableOverloads.Describe(m) });
    }

    // A context that runs each posted callback on the thread pool, with itself as the current context.
    private sealed class ThreadPoolSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    SetSynchronizationContext(this);
                    try
                    {
                        d(state);
                    }
                    finally
                    {
                        SetSynchronizationContext(null);
                    }
                },
                null);
        }
    }
}
