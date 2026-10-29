// <copyright file="Incremental.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times with a precisely incrementing delay.
/// </summary>
/// <remarks>
/// <para>
/// The delay after failed attempt n is <c>initialDelay + (n - 1) * step</c>. With jitter, each delay is multiplied by
/// a random value from 0.75 to 1.25. The delay is then limited to the maximum delay, if one is given. This holds no
/// state for a call, so one instance can be shared.
/// </para>
/// <para>
/// Compare this with <see cref="Backoff"/>, which introduces a random element to the delay to help break contended resources scenarios.
/// </para>
/// </remarks>
public class Incremental : IRetryStrategy
{
    private readonly int maxTries;
    private readonly TimeSpan step;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan? maxDelay;
    private readonly bool useJitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="Incremental"/> class.
    /// </summary>
    /// <remarks>This retries 5 times by default, with an initial delay of 1 second and a step of 2 seconds.</remarks>
    public Incremental()
        : this(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Incremental"/> class.
    /// </summary>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    /// <param name="initialDelay">The initial delay.</param>
    /// <param name="step">The increment step per retry.</param>
    public Incremental(int maxTries, TimeSpan initialDelay, TimeSpan step)
        : this(maxTries, initialDelay, step, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Incremental"/> class.
    /// </summary>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    /// <param name="initialDelay">The initial delay.</param>
    /// <param name="step">The increment step per retry.</param>
    /// <param name="maxDelay">
    /// The longest delay, or <see langword="null"/> for no limit other than the longest delay that a timer supports.
    /// </param>
    /// <param name="useJitter">
    /// <see langword="true"/> to change each delay by a random value between -25% and +25%, before
    /// <paramref name="maxDelay"/> applies.
    /// </param>
    public Incremental(int maxTries, TimeSpan initialDelay, TimeSpan step, TimeSpan? maxDelay, bool useJitter = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, TimeSpan.Zero);
        if (maxDelay is TimeSpan max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, TimeSpan.Zero, nameof(maxDelay));
        }

        this.maxTries = maxTries;
        this.initialDelay = initialDelay;
        this.step = step;
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

        // Calculate in milliseconds as a double, and limit the value before it becomes a TimeSpan, so that a long
        // sequence cannot overflow.
        double delay = ((attempt.AttemptNumber - 1) * this.step.TotalMilliseconds) + this.initialDelay.TotalMilliseconds;
        if (this.useJitter)
        {
            delay = Jitter.Apply(delay);
        }

        return RetryDelay.Clamp(delay, this.maxDelay);
    }
}
