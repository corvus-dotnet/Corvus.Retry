// <copyright file="DoNotRetry.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Provides a <see cref="IRetryStrategy"/> which will always stop retrying immediately.
/// </summary>
public class DoNotRetry : IRetryStrategy
{
    /// <inheritdoc/>
    /// <remarks>This always returns <see langword="null"/>.</remarks>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        return null;
    }
}
