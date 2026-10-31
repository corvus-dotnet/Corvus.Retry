// <copyright file="RecordingStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using Corvus.Retry.Strategies;

/// <summary>
/// A strategy that returns the given delays in order, then <see langword="null"/>, and records every attempt that it
/// is asked about.
/// </summary>
/// <param name="delays">The delay for each retry, in order.</param>
internal sealed class RecordingStrategy(params TimeSpan[] delays) : IRetryStrategy
{
    private readonly object sync = new();
    private readonly List<RetryAttempt> attempts = [];

    /// <summary>
    /// Gets a copy of every attempt that the strategy was asked about, in order.
    /// </summary>
    public List<RetryAttempt> Attempts
    {
        get
        {
            lock (this.sync)
            {
                return [.. this.attempts];
            }
        }
    }

    /// <inheritdoc/>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        lock (this.sync)
        {
            this.attempts.Add(attempt);
        }

        return attempt.AttemptNumber <= delays.Length ? delays[attempt.AttemptNumber - 1] : null;
    }
}
