// <copyright file="LinearTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="Linear"/> strategy allows a fixed number of tries with the same delay before every retry.
/// </summary>
[TestClass]
public class LinearTests
{
    [TestMethod]
    public void ItDelaysByThePeriodicityBeforeEveryRetry()
    {
        TimeSpan periodicity = TimeSpan.FromMilliseconds(250);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(periodicity, 4), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { periodicity, periodicity, periodicity }, delays);
    }

    [TestMethod]
    public void ItRefusesARetryAfterMaxTriesFailedAttempts()
    {
        TimeSpan periodicity = TimeSpan.FromSeconds(1);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(periodicity, 3), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { periodicity, periodicity, null }, delays);
    }

    [TestMethod]
    public void AZeroPeriodicityRetriesWithoutDelay()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(TimeSpan.Zero, 2), 2);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.Zero, null }, delays);
    }

    [TestMethod]
    public void APeriodicityLongerThanATimerSupportsIsLimitedToTheLongestSupportedDelay()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(TimeSpan.MaxValue, 2), 1);

        Assert.AreEqual(RetryDelay.MaxSupported, delays[0]);
    }

    [TestMethod]
    public void TheMaximumDelayApplies()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(TimeSpan.FromSeconds(5), 3, TimeSpan.FromSeconds(2)), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null }, delays);
    }

    [TestMethod]
    public void WithoutAMaximumDelayOrJitter_TheNewConstructorGivesThePeriodicity()
    {
        TimeSpan periodicity = TimeSpan.FromMilliseconds(250);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Linear(periodicity, 3, null), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { periodicity, periodicity, null }, delays);
    }

    [TestMethod]
    public void WithJitter_EachDelayIsWithinTwentyFivePercentOfThePeriodicity()
    {
        var strategy = new Linear(TimeSpan.FromSeconds(1), 6, null, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 6);

            Assert.IsNull(delays[^1]);
            for (int i = 0; i < 5; i++)
            {
                Assert.IsNotNull(delays[i]);
                Assert.IsInRange(TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250), delays[i]!.Value);
            }
        }
    }

    [TestMethod]
    public void WithJitter_ManyDelaysForTheSameAttemptAreSpreadAcrossTheBand()
    {
        var strategy = new Linear(TimeSpan.FromSeconds(1), 2, null, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250));
    }

    [TestMethod]
    public void WithJitter_TheMaximumDelayAppliesAfterTheJitter()
    {
        // The band is [750 ms, 1250 ms], and the maximum cuts its upper half.
        TimeSpan maxDelay = TimeSpan.FromSeconds(1);
        var strategy = new Linear(TimeSpan.FromSeconds(1), 2, maxDelay, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        JitterSamples.AssertLimitedByTheMaximum(delays, TimeSpan.FromMilliseconds(750), maxDelay);
    }

    [TestMethod]
    public void AMaximumDelayOfTimeSpanMaxValueLimitsOnlyToTheLongestSupportedDelay()
    {
        Assert.AreSequenceEqual(
            new TimeSpan?[] { TimeSpan.FromSeconds(1), null },
            StrategyDriver.FailTimes(new Linear(TimeSpan.FromSeconds(1), 2, TimeSpan.MaxValue), 2));
        Assert.AreSequenceEqual(
            new TimeSpan?[] { RetryDelay.MaxSupported, null },
            StrategyDriver.FailTimes(new Linear(TimeSpan.FromDays(60), 2, TimeSpan.MaxValue), 2));
    }

    [TestMethod]
    public void IntMaxValueTriesDoesNotOverflow()
    {
        var strategy = new Linear(TimeSpan.FromSeconds(1), int.MaxValue);

        Assert.AreEqual(TimeSpan.FromSeconds(1), strategy.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, null, null, TimeSpan.FromSeconds(1))));
        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(int.MaxValue, null, null, TimeSpan.FromSeconds(1))));
    }

    [TestMethod]
    public void WhenTheMaximumDelayIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Linear(TimeSpan.FromSeconds(1), 3, TimeSpan.FromMilliseconds(-1)));

        Assert.AreEqual("maxDelay", ex.ParamName);
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        TimeSpan periodicity = TimeSpan.FromSeconds(1);
        var strategy = new Linear(periodicity, 3);

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 3);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { periodicity, periodicity, null }, first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        TimeSpan periodicity = TimeSpan.FromSeconds(1);
        var strategy = new Linear(periodicity, 3);
        TimeSpan?[] expected = [periodicity, periodicity, null];

        List<List<TimeSpan?>> sequences = await StrategyDriver.FailTimesConcurrentlyAsync(strategy, 3).ConfigureAwait(false);

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
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Linear(TimeSpan.FromSeconds(1), maxTries));

        Assert.AreEqual("maxTries", ex.ParamName);
    }

    [TestMethod]
    public void WhenThePeriodicityIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Linear(TimeSpan.FromMilliseconds(-1), 3));

        Assert.AreEqual("periodicity", ex.ParamName);
    }
}
