// <copyright file="JitterSamples.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Takes many delays from a jittering strategy for the same attempt, and asserts that they are spread across the
/// documented band.
/// </summary>
/// <remarks>
/// A band check alone passes for a constant value inside the band, so it cannot detect that the jitter was removed.
/// These checks also require dispersion: with 200 samples from a uniform distribution, the chance that every sample
/// falls in one half of the band is 2^-199, and the chance of fewer than 50 distinct values is negligible.
/// </remarks>
internal static class JitterSamples
{
    /// <summary>
    /// The number of samples to take.
    /// </summary>
    public const int SampleCount = 200;

    /// <summary>
    /// The smallest number of distinct values that the samples must contain.
    /// </summary>
    public const int MinimumDistinctValues = 50;

    /// <summary>
    /// Asks the strategy for the delay after the same attempt many times.
    /// </summary>
    /// <param name="strategy">The strategy.</param>
    /// <param name="attempt">The attempt.</param>
    /// <param name="count">The number of samples.</param>
    /// <returns>The delays. The strategy must not refuse a retry.</returns>
    public static List<TimeSpan> Take(IRetryStrategy strategy, RetryAttempt attempt, int count = SampleCount)
    {
        List<TimeSpan> samples = new(count);
        for (int i = 0; i < count; i++)
        {
            TimeSpan? delay = strategy.GetRetryDelay(attempt);
            Assert.IsNotNull(delay);
            samples.Add(delay.Value);
        }

        return samples;
    }

    /// <summary>
    /// Asserts that every sample lies within the band, that both halves of the band occur, and that the samples
    /// contain at least <see cref="MinimumDistinctValues"/> distinct values.
    /// </summary>
    /// <param name="samples">The samples.</param>
    /// <param name="lower">The lower bound of the band, inclusive.</param>
    /// <param name="upper">The upper bound of the band, inclusive.</param>
    public static void AssertSpreadAcrossTheBand(IReadOnlyList<TimeSpan> samples, TimeSpan lower, TimeSpan upper)
    {
        foreach (TimeSpan sample in samples)
        {
            Assert.IsInRange(lower, upper, sample);
        }

        TimeSpan middle = lower + ((upper - lower) / 2);
        Assert.Contains(d => d < middle, samples, $"No sample was below the middle of the band, {middle}.");
        Assert.Contains(d => d > middle, samples, $"No sample was above the middle of the band, {middle}.");
        Assert.IsGreaterThanOrEqualTo(MinimumDistinctValues, samples.Distinct().Count());
    }

    /// <summary>
    /// Asserts that every sample lies from <paramref name="lower"/> to <paramref name="maximum"/>, that some samples
    /// equal the maximum, and that the samples below the maximum are spread.
    /// </summary>
    /// <param name="samples">The samples, for a band that the maximum cuts.</param>
    /// <param name="lower">The lower bound of the band, inclusive.</param>
    /// <param name="upper">The upper bound of the band before the maximum cuts it, inclusive.</param>
    /// <param name="maximum">The maximum delay, between <paramref name="lower"/> and <paramref name="upper"/>.</param>
    /// <remarks>
    /// <para>
    /// A strategy that returned <see cref="TimeSpan.Zero"/>, or always the maximum, would pass a check of
    /// <c>d &lt;= maximum</c> alone.
    /// </para>
    /// <para>
    /// Only the part of the band below the maximum can show dispersion, so the number of distinct values that the
    /// check requires is half of the number of samples that a uniform jitter puts there. For a band that the maximum
    /// cuts at 30%, 200 samples put about 60 below the maximum, with a standard deviation of about 6.5, so a
    /// requirement of 30 is more than four standard deviations away and does not fail by chance.
    /// </para>
    /// </remarks>
    public static void AssertLimitedByTheMaximum(IReadOnlyList<TimeSpan> samples, TimeSpan lower, TimeSpan upper, TimeSpan maximum)
    {
        Assert.IsInRange(lower, upper, maximum);

        foreach (TimeSpan sample in samples)
        {
            Assert.IsInRange(lower, maximum, sample);
        }

        Assert.Contains(d => d == maximum, samples, $"No sample was limited to the maximum, {maximum}.");

        double fractionBelowTheMaximum = (maximum - lower) / (upper - lower);
        int requiredDistinctValues = Math.Max(2, (int)(samples.Count * fractionBelowTheMaximum / 2));
        Assert.IsGreaterThanOrEqualTo(requiredDistinctValues, samples.Where(d => d < maximum).Distinct().Count());
    }
}
