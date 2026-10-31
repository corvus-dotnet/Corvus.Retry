// <copyright file="RetriableDelayTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// How <see cref="Retriable"/> waits between retries: every delay goes through <see cref="RetryDefaults.TimeProvider"/>.
/// That provider is process-wide, so these tests must not run in parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RetriableDelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private ImmediateTimeProvider time = null!;

    [TestInitialize]
    public void Initialize() => this.time = TimeProviderFixture.Install(new ImmediateTimeProvider());

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheStrategyIsCount_NoDelayIsRequested(RetryShape shape)
    {
        var op = new FlakyOperation(2);

        await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
        Assert.IsEmpty(this.time.Delays);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task EveryRetryWaitsThroughTheInstalledTimeProvider(RetryShape shape)
    {
        // The synchronous loops use Thread.Sleep only with TimeProvider.System; with any other provider they wait for
        // one of its timers, as the asynchronous loops do.
        TimeSpan period = TimeSpan.FromMilliseconds(100);
        var op = new FlakyOperation(3);

        await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(period, 4), new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreEqual(4, op.Invocations);
        Assert.AreSequenceEqual(new[] { period, period, period }, this.time.Delays);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task TheOperationIsRetriedOnlyAfterTheDelay(RetryShape shape)
    {
        var log = new ConcurrentQueue<string>();
        this.time.OnDelay = _ => log.Enqueue("delay");
        var op = new FlakyOperation(2);

        await RetriableDriver.RetryAsync(
            shape,
            () =>
            {
                log.Enqueue("try");
                return op.Invoke();
            },
            CancellationToken.None,
            new Linear(TimeSpan.FromMilliseconds(100), 5),
            new AnyExceptionPolicy()).ConfigureAwait(false);

        Assert.AreSequenceEqual(new[] { "try", "delay", "try", "delay", "try" }, log.ToArray());
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task IncrementalDelaysAreAppliedInSequence(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Incremental(4, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50)), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(4, op.Invocations);
        Assert.AreSequenceEqual(Milliseconds(100, 150, 200), this.time.Delays);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task BackoffDelaysAreAppliedAndIncrease(RetryShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var strategy = new Backoff(4, TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, strategy, new AnyExceptionPolicy())).ConfigureAwait(false);

        IReadOnlyList<TimeSpan> delays = this.time.Delays;
        Assert.HasCount(3, delays);
        Assert.IsGreaterThanOrEqualTo(strategy.MinBackoff, delays[0]);
        Assert.IsGreaterThan(delays[0], delays[1]);
        Assert.IsGreaterThan(delays[1], delays[2]);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheRetryIsAbandoned_NoDelayIsRequestedForTheFinalFailure(RetryShape shape)
    {
        TimeSpan period = TimeSpan.FromMilliseconds(100);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(period, 2), new AnyExceptionPolicy())).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
        Assert.AreEqual(period, Assert.ContainsSingle(this.time.Delays));
    }

    [TestMethod]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WithAFakeTimeProvider_AnAsynchronousRetryStartsOnlyWhenTheDelayHasElapsed(RetryShape shape)
    {
        FakeTimeProvider fake = TimeProviderFixture.Install(new FakeTimeProvider());
        var op = new FlakyOperation(2);

        // The operation completes synchronously, so each delay's timer exists as soon as the failed try returns.
        Task call = shape switch
        {
            RetryShape.AsyncAction => Retriable.RetryAsync(op.InvokeAsync, CancellationToken.None, new Linear(OneHour, 3), new AnyExceptionPolicy()),
            RetryShape.AsyncFunction => Retriable.RetryAsync(op.InvokeAsyncReturning, CancellationToken.None, new Linear(OneHour, 3), new AnyExceptionPolicy()),
            RetryShape.AsyncActionWithToken => Retriable.RetryAsync(_ => op.InvokeAsync(), CancellationToken.None, new Linear(OneHour, 3), new AnyExceptionPolicy()),
            _ => Retriable.RetryAsync(_ => op.InvokeAsyncReturning(), CancellationToken.None, new Linear(OneHour, 3), new AnyExceptionPolicy()),
        };

        Assert.AreEqual(1, op.Invocations);
        fake.Advance(OneHour - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, op.Invocations);
        fake.Advance(TimeSpan.FromTicks(1));
        Assert.AreEqual(2, op.Invocations);
        Assert.IsFalse(call.IsCompleted);
        fake.Advance(OneHour);

        await call.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    public async Task WithAFakeTimeProvider_ASynchronousRetryStartsOnlyWhenTheDelayHasElapsed(RetryShape shape)
    {
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        var op = new FlakyOperation(2);

        // The synchronous loop blocks its thread for the delay, so it runs on the thread pool. The test waits until
        // each delay's timer exists before it moves time on.
        Task call = Task.Run(() => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(OneHour, 3), new AnyExceptionPolicy()));

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.AreEqual(1, op.Invocations);
        fake.Advance(OneHour - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, op.Invocations);
        fake.Advance(TimeSpan.FromTicks(1));

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
        Assert.IsFalse(call.IsCompleted);
        fake.Advance(OneHour);

        await call.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);
        Assert.AreSequenceEqual(new[] { OneHour, OneHour }, fake.Delays);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task WhenTheTokenIsCancelledDuringADelay_TheCallIsCancelledAtOnceAndNoFurtherAttemptStarts(RetryShape shape)
    {
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The synchronous shapes block their thread for the delay, so every shape runs on the thread pool. The test
        // waits until the delay's timer exists before it cancels.
        Task call = Task.Run(() => RetriableDriver.RetryAsync(shape, op.Invoke, cts.Token, new Linear(OneHour, 3), new AnyExceptionPolicy()));

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // The synchronous loop's wait throws OperationCanceledException, and the delay of the asynchronous loop throws
        // TaskCanceledException. Either way, the exception has the caller's token.
        await Task.WhenAny(call).WaitAsync(Timeout).ConfigureAwait(false);
        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => call).ConfigureAwait(false);
        Assert.AreEqual(IsSynchronous(shape) ? typeof(OperationCanceledException) : typeof(TaskCanceledException), thrown.GetType());
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    [DataRow(RetryShape.AsyncAction)]
    [DataRow(RetryShape.AsyncFunction)]
    [DataRow(RetryShape.AsyncActionWithToken)]
    [DataRow(RetryShape.AsyncFunctionWithToken)]
    public async Task TheOnRetryCallbackGetsTheDelayThatTheStrategyChose(RetryShape shape)
    {
        // Each delay is different, so a delay that is dropped, defaulted or taken from another retry is seen.
        var op = new FlakyOperation(3);
        var delays = new List<TimeSpan>();

        await RetriableDriver.RetryAsync(
            shape,
            op.Invoke,
            CancellationToken.None,
            new Incremental(4, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50)),
            new AnyExceptionPolicy(),
            args =>
            {
                lock (delays)
                {
                    delays.Add(args.DelayBeforeRetry);
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.AreSequenceEqual(Milliseconds(100, 150, 200), delays);
        Assert.AreSequenceEqual(Milliseconds(100, 150, 200), this.time.Delays);
    }

    // The changelog: the synchronous Retry uses Thread.Sleep only when the token cannot be cancelled. With a token that
    // can be cancelled, it waits for a timer, so a cancellation stops the wait at once.
    [TestMethod]
    [DataRow(RetryShape.Action)]
    [DataRow(RetryShape.Function)]
    public async Task WithTheSystemTimeProviderAndACancellableToken_ASynchronousRetryWaitsForATimerThatTheTokenStops(RetryShape shape)
    {
        TimeProviderFixture.Install(TimeProvider.System);
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The callback runs before the delay. The token is cancelled there, so the one-hour delay would block the
        // thread if it used Thread.Sleep.
        Task call = Task.Run(
            () => RetriableDriver.RetryAsync(
                shape,
                op.Invoke,
                cts.Token,
                new Linear(OneHour, 3),
                new AnyExceptionPolicy(),
                _ =>
                {
                    cts.Cancel();
                    return ValueTask.CompletedTask;
                }));

        await Task.WhenAny(call).WaitAsync(Timeout).ConfigureAwait(false);
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(1, op.Invocations);
    }

    private static bool IsSynchronous(RetryShape shape) => shape is RetryShape.Action or RetryShape.Function;

    private static TimeSpan[] Milliseconds(params int[] milliseconds)
    {
        return Array.ConvertAll(milliseconds, ms => TimeSpan.FromMilliseconds(ms));
    }
}
