// <copyright file="DelegateRetryStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// A <see cref="IRetryStrategy"/> that asks a delegate for the delay.
/// </summary>
/// <remarks>Created by <see cref="RetryStrategies.FromDelegate(Func{RetryAttempt, TimeSpan?})"/>.</remarks>
internal sealed class DelegateRetryStrategy : IRetryStrategy
{
    private readonly Func<RetryAttempt, TimeSpan?> getRetryDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateRetryStrategy"/> class.
    /// </summary>
    /// <param name="getRetryDelay">The delegate that gives the delay, or <see langword="null"/> to stop.</param>
    internal DelegateRetryStrategy(Func<RetryAttempt, TimeSpan?> getRetryDelay)
    {
        this.getRetryDelay = getRetryDelay;
    }

    /// <inheritdoc/>
    /// <remarks>The delay from the delegate is limited to the range that a timer supports.</remarks>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        return this.getRetryDelay(attempt) is TimeSpan delay ? RetryDelay.Clamp(delay) : null;
    }
}
