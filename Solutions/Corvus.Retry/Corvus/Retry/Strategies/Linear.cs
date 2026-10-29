// <copyright file="Linear.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times with a specific delay.
/// </summary>
/// <remarks>
/// The delay before every retry is the same: the periodicity. This holds no state for a call, so one instance can be
/// shared.
/// </remarks>
public class Linear : IRetryStrategy
{
    private readonly TimeSpan periodicity;
    private readonly int maxTries;

    /// <summary>
    /// Initializes a new instance of the <see cref="Linear"/> class.
    /// </summary>
    /// <param name="periodicity">The delay between tries.</param>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    public Linear(TimeSpan periodicity, int maxTries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periodicity, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);

        this.periodicity = RetryDelay.Clamp(periodicity);
        this.maxTries = maxTries;
    }

    /// <inheritdoc/>
    /// <remarks>This returns the periodicity until the operation has been tried the maximum number of times.</remarks>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        return attempt.AttemptNumber < this.maxTries ? this.periodicity : null;
    }
}
