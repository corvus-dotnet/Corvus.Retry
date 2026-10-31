// <copyright file="IRetryStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using Corvus.Retry.Policies;

/// <summary>
/// A strategy which determines how an operation will be retried
/// when the operation failed.
/// </summary>
/// <remarks>
/// <para>
/// Compare this with <see cref="IRetryPolicy"/> which determines whether a retry will be executed.
/// </para>
/// <para>
/// A strategy holds no state for a call: everything it needs is in the <see cref="RetryAttempt"/>. One instance can
/// therefore be shared, and used by many calls at the same time.
/// </para>
/// </remarks>
public interface IRetryStrategy
{
    /// <summary>
    /// Gets the delay before the next attempt, or <see langword="null"/> to stop retrying.
    /// </summary>
    /// <param name="attempt">The attempt that failed.</param>
    /// <returns>The delay before the next attempt, or <see langword="null"/> if the operation must not be retried.</returns>
    TimeSpan? GetRetryDelay(in RetryAttempt attempt);
}
