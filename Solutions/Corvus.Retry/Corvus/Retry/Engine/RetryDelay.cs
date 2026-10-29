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
    /// A zero delay returns at once. If <see cref="CanUseThreadSleep"/> returns <see langword="true"/>, this uses
    /// <see cref="Thread.Sleep(TimeSpan)"/>. Otherwise it waits for a timer from the provider, so that a test's time
    /// provider controls the wait.
    /// </remarks>
    internal static void Wait(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        delay = Clamp(delay);

        if (CanUseThreadSleep(delay, timeProvider, cancellationToken))
        {
            Thread.Sleep(delay);
            return;
        }

        using ManualResetEventSlim gate = new();
        using ITimer timer = timeProvider.CreateTimer(static s => ((ManualResetEventSlim)s!).Set(), gate, delay, Timeout.InfiniteTimeSpan);
        gate.Wait(Timeout.Infinite, cancellationToken);
    }

    /// <summary>
    /// Gets a value that tells whether <see cref="Wait"/> can use <see cref="Thread.Sleep(TimeSpan)"/> for a delay.
    /// </summary>
    /// <param name="delay">The delay, already limited by <see cref="Clamp(TimeSpan)"/>.</param>
    /// <param name="timeProvider">The time provider with which to measure the delay.</param>
    /// <param name="cancellationToken">The token with which to stop the wait.</param>
    /// <returns>
    /// <see langword="true"/> if the wait cannot be cancelled, the provider is <see cref="TimeProvider.System"/>, and
    /// the delay is not longer than <see cref="int.MaxValue"/> milliseconds (about 24.9 days).
    /// </returns>
    /// <remarks>
    /// <see cref="Thread.Sleep(TimeSpan)"/> throws for a delay longer than <see cref="int.MaxValue"/> milliseconds,
    /// but a timer supports delays up to <see cref="MaxSupported"/>. A longer delay uses a timer.
    /// </remarks>
    internal static bool CanUseThreadSleep(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        return !cancellationToken.CanBeCanceled
            && ReferenceEquals(timeProvider, TimeProvider.System)
            && delay.TotalMilliseconds <= int.MaxValue;
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

    /// <summary>
    /// Limits a delay calculated in milliseconds to an optional maximum, and then to the range that a timer supports.
    /// </summary>
    /// <param name="milliseconds">The delay in milliseconds.</param>
    /// <param name="maxDelay">The longest delay that the caller allows, or <see langword="null"/> for no limit.</param>
    /// <returns>The limited delay.</returns>
    /// <remarks>
    /// Strategies calculate delays as a <see langword="double"/>, so that a long sequence cannot overflow. This limits
    /// the value before it becomes a <see cref="TimeSpan"/>. A value that is not a number becomes
    /// <see cref="TimeSpan.Zero"/>.
    /// </remarks>
    internal static TimeSpan Clamp(double milliseconds, TimeSpan? maxDelay)
    {
        if (double.IsNaN(milliseconds) || milliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        double limit = maxDelay is TimeSpan max && max < MaxSupported ? max.TotalMilliseconds : MaxSupported.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, limit));
    }
}
