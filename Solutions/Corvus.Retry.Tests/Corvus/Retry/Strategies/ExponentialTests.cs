// <copyright file="ExponentialTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="Exponential"/> strategy doubles the delay before each retry, with an optional maximum and optional
/// decorrelated jitter.
/// </summary>
/// <remarks>
/// Without jitter, the delay after failed attempt n is <c>baseDelay * 2^(n - 1)</c>. With jitter, it is
/// <c>min(maxDelay, random(baseDelay, previous * 3))</c>.
/// </remarks>
[TestClass]
public class ExponentialTests
{
    [TestMethod]
    public void WithoutJitter_TheDelayDoublesBeforeEachRetry()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Exponential(4, TimeSpan.FromSeconds(1)), 4);

        Assert.AreSequenceEqual(Seconds(1, 2, 4, null), delays);
    }

    [TestMethod]
    public void WithoutJitter_EachDelayIsTheBaseDelayTimesTheExactPowerOfTwo()
    {
        var strategy = new Exponential(int.MaxValue, TimeSpan.FromMilliseconds(3));

        // The range goes past the attempt where the power of 2 is infinite as a double. The cap is stated literally,
        // rather than through the production helper, so that a change to the helper cannot change the oracle.
        TimeSpan cap = TimeSpan.FromMilliseconds(0xFFFFFFFE);
        for (int attempt = 1; attempt <= 1100; attempt++)
        {
            double milliseconds = 3 * Math.Pow(2, attempt - 1);
            TimeSpan expected = milliseconds < 0xFFFFFFFE ? TimeSpan.FromMilliseconds(milliseconds) : cap;
            Assert.AreEqual(expected, strategy.GetRetryDelay(new RetryAttempt(attempt, null, null, TimeSpan.Zero)), $"Attempt {attempt}");
        }
    }

    [TestMethod]
    public void TheMaximumDelayApplies()
    {
        var strategy = new Exponential(6, TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(3));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 6);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, 3, 3, null), delays);
    }

    [TestMethod]
    public void WhenNoRetriesRemain_ItRefusesARetry()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Exponential(2, TimeSpan.FromSeconds(1)), 2);

        Assert.AreSequenceEqual(Seconds(1, null), delays);
    }

    [TestMethod]
    public void WithJitter_EachDelayIsBetweenTheBaseDelayAndThreeTimesThePreviousDelayLimitedToTheMaximum()
    {
        TimeSpan baseDelay = TimeSpan.FromMilliseconds(100);
        TimeSpan maxDelay = TimeSpan.FromSeconds(2);
        var strategy = new Exponential(10, baseDelay, maxDelay, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 9);

            TimeSpan previous = baseDelay;
            foreach (TimeSpan? delay in delays)
            {
                TimeSpan upper = previous * 3 < maxDelay ? previous * 3 : maxDelay;
                Assert.IsNotNull(delay);
                Assert.IsInRange(baseDelay, upper, delay.Value);
                previous = delay.Value;
            }
        }
    }

    [TestMethod]
    public void WithJitter_ItStillRefusesARetryAfterMaxTriesFailedAttempts()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Exponential(3, TimeSpan.FromSeconds(1), useJitter: true), 3);

        Assert.HasCount(3, delays);
        Assert.IsInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), delays[0]!.Value);
        Assert.IsInRange(TimeSpan.FromSeconds(1), delays[0]!.Value * 3, delays[1]!.Value);
        Assert.IsNull(delays[2]);
    }

    [TestMethod]
    public void WithJitter_ManyDelaysForTheSameAttemptAreSpreadAcrossTheBand()
    {
        // Before the first retry, previous is the base delay, so the band is [base, 3 * base].
        var strategy = new Exponential(3, TimeSpan.FromSeconds(1), useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public void WithJitter_ALongerPreviousDelayAllowsALongerNextDelay()
    {
        // PreviousDelay is the only input that changes. Only a strategy that uses it can return more than 3 * base.
        TimeSpan baseDelay = TimeSpan.FromMilliseconds(100);
        var strategy = new Exponential(10, baseDelay, useJitter: true);

        List<TimeSpan> afterLong = JitterSamples.Take(strategy, new RetryAttempt(5, new InvalidOperationException(), null, TimeSpan.FromSeconds(10)));
        List<TimeSpan> first = JitterSamples.Take(strategy, new RetryAttempt(5, new InvalidOperationException(), null, TimeSpan.Zero));

        foreach (TimeSpan delay in afterLong)
        {
            Assert.IsInRange(baseDelay, TimeSpan.FromSeconds(30), delay);
        }

        Assert.Contains(d => d > baseDelay * 3, afterLong);

        // A zero PreviousDelay means that previous is the base delay.
        foreach (TimeSpan delay in first)
        {
            Assert.IsInRange(baseDelay, baseDelay * 3, delay);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void WhenTheMaximumDelayIsShorterThanTheBaseDelay_EveryDelayIsTheMaximum(bool useJitter)
    {
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(500);
        var strategy = new Exponential(4, TimeSpan.FromSeconds(1), maxDelay, useJitter);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            Assert.AreSequenceEqual(new TimeSpan?[] { maxDelay, maxDelay, maxDelay, null }, StrategyDriver.FailTimes(strategy, 4));
        }
    }

    [TestMethod]
    public void AMaximumDelayOfTimeSpanMaxValueLimitsOnlyToTheLongestSupportedDelay()
    {
        var strategy = new Exponential(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.MaxValue);

        Assert.AreEqual(TimeSpan.FromSeconds(4), strategy.GetRetryDelay(new RetryAttempt(3, null, null, TimeSpan.FromSeconds(2))));
        Assert.AreEqual(RetryDelay.MaxSupported, strategy.GetRetryDelay(new RetryAttempt(5000, null, null, TimeSpan.FromSeconds(2))));
    }

    [TestMethod]
    public void AZeroBaseDelayRetriesWithoutDelay()
    {
        var strategy = new Exponential(int.MaxValue, TimeSpan.Zero, useJitter: false);

        Assert.AreEqual(TimeSpan.Zero, strategy.GetRetryDelay(new RetryAttempt(1, null, null, TimeSpan.Zero)));
        Assert.AreEqual(TimeSpan.Zero, strategy.GetRetryDelay(new RetryAttempt(5000, null, null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void ALongSequenceDoesNotOverflowAndIsLimitedToTheLongestSupportedDelay()
    {
        var strategy = new Exponential(int.MaxValue, TimeSpan.FromSeconds(1));

        Assert.AreEqual(RetryDelay.MaxSupported, strategy.GetRetryDelay(new RetryAttempt(5000, null, null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WithJitter_ALongPreviousDelayIsLimitedToTheLongestSupportedDelay()
    {
        var strategy = new Exponential(int.MaxValue, TimeSpan.FromDays(40), useJitter: true);

        // random(40 d, 3 * 49.7 d) is limited to 49.7 d, which most samples reach; none may exceed it.
        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(2, null, null, RetryDelay.MaxSupported));

        foreach (TimeSpan delay in delays)
        {
            Assert.IsInRange(TimeSpan.FromDays(40), TimeSpan.FromMilliseconds(0xFFFFFFFE), delay);
        }

        Assert.Contains(d => d == TimeSpan.FromMilliseconds(0xFFFFFFFE), delays);
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        var strategy = new Exponential(4, TimeSpan.FromSeconds(1));

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 4);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 4);

        Assert.AreSequenceEqual(Seconds(1, 2, 4, null), first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        var strategy = new Exponential(4, TimeSpan.FromSeconds(1));
        TimeSpan?[] expected = Seconds(1, 2, 4, null);

        List<List<TimeSpan?>> sequences = await StrategyDriver.FailTimesConcurrentlyAsync(strategy, 4).ConfigureAwait(false);

        foreach (List<TimeSpan?> sequence in sequences)
        {
            Assert.AreSequenceEqual(expected, sequence);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void WhenMaxTriesIsNotPositive_TheConstructorThrows(int maxTries)
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Exponential(maxTries, TimeSpan.FromSeconds(1)));

        Assert.AreEqual("maxTries", ex.ParamName);
    }

    [TestMethod]
    public void WhenTheBaseDelayIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Exponential(3, TimeSpan.FromSeconds(-1)));

        Assert.AreEqual("baseDelay", ex.ParamName);
    }

    [TestMethod]
    public void WhenTheMaximumDelayIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));

        Assert.AreEqual("maxDelay", ex.ParamName);
    }

    private static TimeSpan?[] Seconds(params int?[] seconds)
    {
        return Array.ConvertAll(seconds, s => s is int value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null);
    }
}
