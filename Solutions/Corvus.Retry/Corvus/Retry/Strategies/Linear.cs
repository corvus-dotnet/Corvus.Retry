// <copyright file="Linear.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times with a specific delay.
/// </summary>
/// <remarks>
/// <para>
/// The delay before every retry is the same: the periodicity. With jitter, each delay is the periodicity multiplied
/// by a random value from 0.75 to 1.25. The delay is then limited to the maximum delay, if one is given.
/// </para>
/// <para>
/// Compare this with <see cref="RetryStrategies.Linear(int, TimeSpan, TimeSpan?, bool)"/>, which increases the delay
/// before each retry (<c>delay * n</c>). This holds no state for a call, so one instance can be shared.
/// </para>
/// </remarks>
public class Linear : IRetryStrategy
{
    private readonly TimeSpan periodicity;
    private readonly int maxTries;
    private readonly TimeSpan? maxDelay;
    private readonly bool useJitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="Linear"/> class.
    /// </summary>
    /// <param name="periodicity">The delay between tries.</param>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    public Linear(TimeSpan periodicity, int maxTries)
        : this(periodicity, maxTries, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Linear"/> class.
    /// </summary>
    /// <param name="periodicity">The delay between tries.</param>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    /// <param name="maxDelay">
    /// The longest delay, or <see langword="null"/> for no limit other than the longest delay that a timer supports.
    /// </param>
    /// <param name="useJitter">
    /// <see langword="true"/> to change each delay by a random value between -25% and +25%, before
    /// <paramref name="maxDelay"/> applies.
    /// </param>
    public Linear(TimeSpan periodicity, int maxTries, TimeSpan? maxDelay, bool useJitter = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periodicity, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);
        if (maxDelay is TimeSpan max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, TimeSpan.Zero, nameof(maxDelay));
        }

        // Without jitter the delay never changes, so it is limited once, here.
        this.periodicity = useJitter ? periodicity : RetryDelay.Clamp(periodicity.TotalMilliseconds, maxDelay);
        this.maxTries = maxTries;
        this.maxDelay = maxDelay;
        this.useJitter = useJitter;
    }

    /// <inheritdoc/>
    /// <remarks>This returns the periodicity until the operation has been tried the maximum number of times.</remarks>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= this.maxTries)
        {
            return null;
        }

        return this.useJitter
            ? RetryDelay.Clamp(Jitter.Apply(this.periodicity.TotalMilliseconds), this.maxDelay)
            : this.periodicity;
    }
}
