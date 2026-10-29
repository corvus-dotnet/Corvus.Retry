// <copyright file="RetryDelayTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryDelay"/> waits for a delay with the <see cref="TimeProvider"/> it is given: a zero delay creates no
/// timer, a delay ends only when the provider's time has moved on, and cancellation ends the wait early.
/// </summary>
/// <remarks>
/// Each test passes its own provider, so these tests run in parallel with others.
/// </remarks>
[TestClass]
public class RetryDelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestTimerDelay = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    [TestMethod]
    public void WhenTheDelayIsZero_WaitAsyncReturnsACompletedTaskAndCreatesNoTimer()
    {
        RecordingFakeTimeProvider fake = new();

        Task wait = RetryDelay.WaitAsync(TimeSpan.Zero, fake, CancellationToken.None);

        Assert.IsTrue(wait.IsCompletedSuccessfully);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public void WhenTheDelayIsZero_WaitReturnsAtOnceAndCreatesNoTimer()
    {
        RecordingFakeTimeProvider fake = new();

        RetryDelay.Wait(TimeSpan.Zero, fake, CancellationToken.None);

        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public async Task WaitAsyncCompletesOnlyWhenTheDelayHasElapsed()
    {
        RecordingFakeTimeProvider fake = new();

        Task wait = RetryDelay.WaitAsync(OneHour, fake, CancellationToken.None);

        fake.Advance(OneHour - TimeSpan.FromTicks(1));
        Assert.IsFalse(wait.IsCompleted);
        fake.Advance(TimeSpan.FromTicks(1));

        await wait.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
    }

    [TestMethod]
    public async Task WaitBlocksUntilTheDelayHasElapsed()
    {
        RecordingFakeTimeProvider fake = new();

        Task wait = Task.Run(() => RetryDelay.Wait(OneHour, fake, CancellationToken.None));

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(OneHour - TimeSpan.FromTicks(1));
        Assert.IsFalse(wait.IsCompleted);
        fake.Advance(TimeSpan.FromTicks(1));

        await wait.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
    }

    [TestMethod]
    public async Task WhenTheTokenIsCancelled_WaitAsyncIsCancelledWithoutWaitingForTheDelayToEnd()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cancellation = new();

        Task wait = RetryDelay.WaitAsync(OneHour, fake, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(wait.IsCanceled);
    }

    [TestMethod]
    public async Task WhenTheTokenIsCancelled_WaitThrowsWithoutWaitingForTheDelayToEnd()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cancellation = new();

        Task wait = Task.Run(() => RetryDelay.Wait(OneHour, fake, cancellation.Token));
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(Timeout)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WaitAsyncShortensADelayLongerThanATimerSupports()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cancellation = new();

        Task wait = RetryDelay.WaitAsync(TimeSpan.FromDays(60), fake, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { LongestTimerDelay }, fake.Delays);
    }

    [TestMethod]
    public async Task WaitShortensADelayLongerThanATimerSupports()
    {
        RecordingFakeTimeProvider fake = new();
        using CancellationTokenSource cancellation = new();

        Task wait = Task.Run(() => RetryDelay.Wait(TimeSpan.FromDays(60), fake, cancellation.Token));
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { LongestTimerDelay }, fake.Delays);
    }
}
