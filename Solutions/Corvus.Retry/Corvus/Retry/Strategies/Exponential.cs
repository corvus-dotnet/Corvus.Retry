// <copyright file="Exponential.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times, and doubles the delay before
/// each retry.
/// </summary>
/// <remarks>
/// <para>
/// Without jitter, the delay after failed attempt n is <c>baseDelay * 2^(n - 1)</c>, limited to the maximum delay.
/// </para>
/// <para>
/// With jitter, the delay is <c>min(maxDelay, random(baseDelay, previous * 3))</c>, where <c>previous</c> is
/// <see cref="RetryAttempt.PreviousDelay"/> (or <c>baseDelay</c> before the first retry). This algorithm is known as
/// decorrelated jitter. It spreads the retries of many callers that failed at the same time.
/// </para>
/// <para>
/// Every delay is also limited to the longest delay that a timer supports. This holds no state for a call, so one
/// instance can be shared.
/// </para>
/// </remarks>
public class Exponential : IRetryStrategy
{
    private readonly int maxTries;
    private readonly TimeSpan baseDelay;
    private readonly TimeSpan? maxDelay;
    private readonly bool useJitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="Exponential"/> class.
    /// </summary>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    /// <param name="baseDelay">The delay before the first retry.</param>
    /// <param name="maxDelay">
    /// The longest delay, or <see langword="null"/> for no limit other than the longest delay that a timer supports.
    /// </param>
    /// <param name="useJitter"><see langword="true"/> to use decorrelated jitter.</param>
    public Exponential(int maxTries, TimeSpan baseDelay, TimeSpan? maxDelay = null, bool useJitter = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        if (maxDelay is TimeSpan max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, TimeSpan.Zero, nameof(maxDelay));
        }

        this.maxTries = maxTries;
        this.baseDelay = baseDelay;
        this.maxDelay = maxDelay;
        this.useJitter = useJitter;
    }

    /// <inheritdoc/>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= this.maxTries)
        {
            return null;
        }

        double baseMilliseconds = this.baseDelay.TotalMilliseconds;

        // A zero base delay is checked first, because an infinite power of 2 times 0 is not a number.
        if (baseMilliseconds == 0)
        {
            return TimeSpan.Zero;
        }

        // Calculate in milliseconds as a double, and limit the value before it becomes a TimeSpan, so that a long
        // sequence cannot overflow.
        double delay;
        if (this.useJitter)
        {
            double previous = attempt.PreviousDelay == TimeSpan.Zero ? baseMilliseconds : attempt.PreviousDelay.TotalMilliseconds;
            delay = baseMilliseconds + (Random.Shared.NextDouble() * ((previous * 3) - baseMilliseconds));
        }
        else
        {
            delay = baseMilliseconds * Math.Pow(2, attempt.AttemptNumber - 1);
        }

        return RetryDelay.Clamp(delay, this.maxDelay);
    }
}
