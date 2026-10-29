// <copyright file="Backoff.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which increases the delay between retries on each occurrence.
/// </summary>
/// <remarks>
/// <para>
/// The delay after failed attempt n is <c>min(MinBackoff + (2^n - 1) * r, MaxBackoff)</c>, where r is a random time
/// from <c>0.8 * DeltaBackoff</c> to <c>1.2 * DeltaBackoff</c> (+/-20%), at every scale of
/// <see cref="DeltaBackoff"/>. Each delay takes a new r, so callers that failed at the same time do not retry at the
/// same time. You can set the bounds with <see cref="MinBackoff"/> and <see cref="MaxBackoff"/>. Every delay is also
/// limited to the longest delay that a timer supports.
/// </para>
/// <para>Compare this with <see cref="Incremental"/>, which provides a simply incrementing delay, and is therefore prone to contended resource race conditions.</para>
/// <para>This holds no state for a call, so one instance can be shared.</para>
/// </remarks>
public class Backoff : IRetryStrategy
{
    private readonly int maxTries;

    /// <summary>
    /// Initialises a new instance of the <see cref="Backoff"/> class.
    /// </summary>
    /// <remarks>This defaults to 5 retries, with a back-off of 2 seconds. The minimum backoff is 1 second, and the maximum backoff is 30 seconds.</remarks>
    public Backoff()
        : this(5, TimeSpan.FromSeconds(2))
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="Backoff"/> class.
    /// </summary>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    /// <param name="deltaBackoff">The increase in the delay with each try.</param>
    /// <remarks>The  minimum backoff is 1 second, and the maximum backoff is 30 seconds.</remarks>
    public Backoff(int maxTries, TimeSpan deltaBackoff)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);
        ArgumentOutOfRangeException.ThrowIfLessThan(deltaBackoff, TimeSpan.Zero);

        this.maxTries = maxTries;
        this.DeltaBackoff = deltaBackoff;
        this.MinBackoff = this.DefaultMinBackoff;
        this.MaxBackoff = this.DefaultMaxBackoff;
    }

    /// <summary>
    /// Gets the default minimum backoff time.
    /// </summary>
    /// <remarks>This is currently 1 second.</remarks>
    public TimeSpan DefaultMinBackoff
    {
        get { return TimeSpan.FromSeconds(1); }
    }

    /// <summary>
    /// Gets the default maximum backoff time.
    /// </summary>
    /// <remarks>This is currently 30 seconds.</remarks>
    public TimeSpan DefaultMaxBackoff
    {
        get { return TimeSpan.FromSeconds(30); }
    }

    /// <summary>
    /// Gets the amount by which the delay increases with each retry.
    /// </summary>
    public TimeSpan DeltaBackoff { get; }

    /// <summary>
    /// Gets the minimum delay.
    /// </summary>
    /// <remarks>Set this with an object initialiser. It cannot change after the strategy is created.</remarks>
    public TimeSpan MinBackoff { get; init; }

    /// <summary>
    /// Gets the maximum delay.
    /// </summary>
    /// <remarks>Set this with an object initialiser. It cannot change after the strategy is created.</remarks>
    public TimeSpan MaxBackoff { get; init; }

    /// <inheritdoc/>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= this.maxTries)
        {
            return null;
        }

        // r is calculated as a double, so that the band holds for a delta of a few milliseconds (a whole number
        // would collapse to a constant) and for a delta longer than int.MaxValue milliseconds (an int would saturate).
        // Random.Shared is safe for many threads, and gives each caller a different value.
        double jitter = this.DeltaBackoff.TotalMilliseconds * (0.8 + (0.4 * Random.Shared.NextDouble()));

        // Calculate in milliseconds as a double, and limit the value before it becomes a TimeSpan, so that a long
        // sequence cannot overflow. A zero jitter is checked first, because an infinite power of 2 times 0 is not a number.
        // ScaleB gives the same exact power of 2 as Math.Pow, without the cost of a general power.
        double increment = jitter == 0 ? 0 : (Math.ScaleB(1, attempt.AttemptNumber) - 1) * jitter;

        return RetryDelay.Clamp(this.MinBackoff.TotalMilliseconds + increment, this.MaxBackoff);
    }
}
