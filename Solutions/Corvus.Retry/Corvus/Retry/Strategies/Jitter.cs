// <copyright file="Jitter.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Adds a random variation to a delay, so that many callers that failed at the same time do not retry at the same time.
/// </summary>
internal static class Jitter
{
    /// <summary>
    /// Changes a delay by a random value between -25% and +25%.
    /// </summary>
    /// <param name="milliseconds">The delay in milliseconds.</param>
    /// <returns>The delay multiplied by a random value from 0.75 to 1.25.</returns>
    /// <remarks>This uses <see cref="Random.Shared"/>, which is safe for many threads.</remarks>
    internal static double Apply(double milliseconds)
    {
        return milliseconds * (0.75 + (0.5 * Random.Shared.NextDouble()));
    }
}
