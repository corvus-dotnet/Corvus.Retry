// <copyright file="RetryDelay.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Waits for the delay before a retry, using a <see cref="TimeProvider"/>.
/// </summary>
internal static class RetryDelay
{
    // The longest delay that a timer supports. Task.Delay throws for a longer delay, so longer delays are shortened.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    /// <summary>
    /// Returns a task that completes when the delay has elapsed.
    /// </summary>
    /// <param name="delay">The delay. A delay longer than a timer supports is shortened to the longest supported delay.</param>
    /// <param name="timeProvider">The time provider with which to measure the delay.</param>
    /// <param name="cancellationToken">The token with which to stop the wait.</param>
    /// <returns>A task that completes when the delay has elapsed, or is cancelled when the token is cancelled first.</returns>
    /// <remarks>A zero delay returns a completed task, and creates no timer.</remarks>
    internal static Task WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return Task.Delay(Shorten(delay), timeProvider, cancellationToken);
    }

    /// <summary>
    /// Blocks the current thread until the delay has elapsed.
    /// </summary>
    /// <param name="delay">The delay. A delay longer than a timer supports is shortened to the longest supported delay.</param>
    /// <param name="timeProvider">The time provider with which to measure the delay.</param>
    /// <param name="cancellationToken">The token with which to stop the wait.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled before the delay elapsed.</exception>
    /// <remarks>
    /// A zero delay returns at once. If the wait cannot be cancelled and the provider is
    /// <see cref="TimeProvider.System"/>, this uses <see cref="Thread.Sleep(TimeSpan)"/>. Otherwise it waits for a timer
    /// from the provider, so that a test's time provider controls the wait.
    /// </remarks>
    internal static void Wait(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        delay = Shorten(delay);

        if (!cancellationToken.CanBeCanceled && ReferenceEquals(timeProvider, TimeProvider.System))
        {
            Thread.Sleep(delay);
            return;
        }

        using ManualResetEventSlim gate = new();
        using ITimer timer = timeProvider.CreateTimer(static s => ((ManualResetEventSlim)s!).Set(), gate, delay, Timeout.InfiniteTimeSpan);
        gate.Wait(Timeout.Infinite, cancellationToken);
    }

    private static TimeSpan Shorten(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;
}
