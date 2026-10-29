// <copyright file="StrategyDriver.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;

/// <summary>
/// Drives an <see cref="IRetryStrategy"/> through a series of failures, as a retry loop would.
/// </summary>
internal static class StrategyDriver
{
    /// <summary>
    /// Reports a number of failures to the strategy.
    /// </summary>
    /// <param name="strategy">The strategy.</param>
    /// <param name="failures">The number of failures to report.</param>
    /// <returns>The delay the strategy requested after each failure.</returns>
    public static List<TimeSpan> FailTimes(IRetryStrategy strategy, int failures)
    {
        List<TimeSpan> delays = [];
        for (int i = 0; i < failures; i++)
        {
            delays.Add(strategy.PrepareToRetry(new InvalidOperationException($"Failure {i + 1}")));
        }

        return delays;
    }
}
