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
    /// <summary>
    /// The longest delay that a timer supports: 0xFFFFFFFE milliseconds, about 49.7 days.
    /// </summary>
    /// <remarks>
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> and a timed
    /// <see cref="CancellationTokenSource"/> throw for a longer time, so every delay is limited to this value.
    /// </remarks>
    internal static readonly TimeSpan MaxSupported = TimeSpan.FromMilliseconds(0xFFFFFFFE);

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

        return Task.Delay(Clamp(delay), timeProvider, cancellationToken);
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

        delay = Clamp(delay);

        if (!cancellationToken.CanBeCanceled && ReferenceEquals(timeProvider, TimeProvider.System))
        {
            Thread.Sleep(delay);
            return;
        }

        using ManualResetEventSlim gate = new();
        using ITimer timer = timeProvider.CreateTimer(static s => ((ManualResetEventSlim)s!).Set(), gate, delay, Timeout.InfiniteTimeSpan);
        gate.Wait(Timeout.Infinite, cancellationToken);
    }

    /// <summary>
    /// Limits a delay to the range that a timer supports.
    /// </summary>
    /// <param name="delay">The delay.</param>
    /// <returns>
    /// <see cref="TimeSpan.Zero"/> for a negative delay, <see cref="MaxSupported"/> for a longer delay, and otherwise
    /// <paramref name="delay"/>.
    /// </returns>
    internal static TimeSpan Clamp(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaxSupported ? MaxSupported : delay;
    }
}
