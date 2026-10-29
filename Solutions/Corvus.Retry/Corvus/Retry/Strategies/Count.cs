// <copyright file="Count.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times without delay.
/// </summary>
/// <remarks>This holds no state for a call, so one instance can be shared.</remarks>
public class Count : IRetryStrategy
{
    private readonly int maxTries;

    /// <summary>
    /// Initializes a new instance of the <see cref="Count"/> class.
    /// </summary>
    /// <remarks>This tries the operation up to 5 times.</remarks>
    public Count()
        : this(5)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Count"/> class.
    /// </summary>
    /// <param name="maxTries">The maximum number of times the method will be tried.</param>
    public Count(int maxTries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);

        this.maxTries = maxTries;
    }

    /// <inheritdoc/>
    /// <remarks>This returns <see cref="TimeSpan.Zero"/> until the operation has been tried the maximum number of times.</remarks>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        return attempt.AttemptNumber < this.maxTries ? TimeSpan.Zero : null;
    }
}
