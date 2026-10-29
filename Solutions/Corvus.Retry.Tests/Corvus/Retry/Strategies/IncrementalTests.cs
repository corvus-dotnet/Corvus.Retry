// <copyright file="IncrementalTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="Incremental"/> strategy increases the delay by a fixed step before each retry.
/// </summary>
/// <remarks>
/// The delay after failed attempt n is <c>initialDelay + (n - 1) * step</c>.
/// </remarks>
[TestClass]
public class IncrementalTests
{
    [TestMethod]
    public void ItIncreasesTheDelayByTheStepOnEachRetry()
    {
        // The README example: 5 tries, starting at 1 second, increasing by 1 second each time.
        var strategy = new Incremental(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 5);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, 4, null), delays);
    }

    [TestMethod]
    public void WhenConstructedWithoutArguments_ItAllowsFiveTriesStartingAtOneSecondInTwoSecondSteps()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Incremental(), 5);

        Assert.AreSequenceEqual(Seconds(1, 3, 5, 7, null), delays);
    }

    [TestMethod]
    public void WhenNoRetriesRemain_ItRefusesARetry()
    {
        var strategy = new Incremental(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 2);

        Assert.AreSequenceEqual(Seconds(1, null), delays);
    }

    [TestMethod]
    public void TheParametersCanBeNamed()
    {
        var strategy = new Incremental(maxTries: 3, initialDelay: TimeSpan.FromSeconds(2), step: TimeSpan.FromSeconds(3));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 3);

        Assert.AreSequenceEqual(Seconds(2, 5, null), delays);
    }

    [TestMethod]
    public void ADelayLongerThanATimerSupportsIsLimitedToTheLongestSupportedDelay()
    {
        var strategy = new Incremental(int.MaxValue, TimeSpan.FromDays(40), TimeSpan.FromDays(40));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 2);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.FromDays(40), RetryDelay.MaxSupported }, delays);
    }

    [TestMethod]
    public void TheMaximumDelayApplies()
    {
        var strategy = new Incremental(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 6);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, 3, 3, null), delays);
    }

    [TestMethod]
    public void WithoutAMaximumDelayOrJitter_TheNewConstructorGivesTheSameDelays()
    {
        var strategy = new Incremental(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), null);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 5);

        Assert.AreSequenceEqual(Seconds(1, 3, 5, 7, null), delays);
    }

    [TestMethod]
    public void WithJitter_EachDelayIsWithinTwentyFivePercentOfTheDelayWithoutJitter()
    {
        var strategy = new Incremental(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 6);

            Assert.IsNull(delays[^1]);
            for (int retry = 1; retry <= 5; retry++)
            {
                TimeSpan? delay = delays[retry - 1];
                Assert.IsNotNull(delay);
                Assert.IsInRange(TimeSpan.FromMilliseconds(750 * retry), TimeSpan.FromMilliseconds(1250 * retry), delay.Value);
            }
        }
    }

    [TestMethod]
    public void WithJitter_ManyDelaysForTheSameAttemptAreSpreadAcrossTheBand()
    {
        // After attempt 2 the delay without jitter is 2 s, so the band is [1.5 s, 2.5 s].
        var strategy = new Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(2, new InvalidOperationException(), null, TimeSpan.FromSeconds(1)));

        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500));
    }

    [TestMethod]
    public void WithJitter_TheMaximumDelayAppliesAfterTheJitter()
    {
        TimeSpan maxDelay = TimeSpan.FromSeconds(2);
        var strategy = new Incremental(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), maxDelay, useJitter: true);

        // Attempt 1: the band [0.75 s, 1.25 s] lies below the maximum.
        JitterSamples.AssertSpreadAcrossTheBand(
            JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)),
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromMilliseconds(1250));

        // Attempt 2: the maximum cuts the band [1.5 s, 2.5 s].
        JitterSamples.AssertLimitedByTheMaximum(
            JitterSamples.Take(strategy, new RetryAttempt(2, new InvalidOperationException(), null, TimeSpan.FromSeconds(1))),
            TimeSpan.FromMilliseconds(1500),
            maxDelay);

        // Attempts 3 to 5: the whole band lies above the maximum.
        for (int attempt = 3; attempt <= 5; attempt++)
        {
            foreach (TimeSpan delay in JitterSamples.Take(strategy, new RetryAttempt(attempt, new InvalidOperationException(), null, maxDelay)))
            {
                Assert.AreEqual(maxDelay, delay);
            }
        }
    }

    [TestMethod]
    public void AMaximumDelayOfTimeSpanMaxValueLimitsOnlyToTheLongestSupportedDelay()
    {
        var strategy = new Incremental(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.MaxValue);

        Assert.AreEqual(TimeSpan.FromSeconds(3), strategy.GetRetryDelay(new RetryAttempt(3, null, null, TimeSpan.FromSeconds(2))));
        Assert.AreEqual(RetryDelay.MaxSupported, strategy.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, null, null, TimeSpan.FromSeconds(2))));
    }

    [TestMethod]
    public void WhenTheMaximumDelayIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));

        Assert.AreEqual("maxDelay", ex.ParamName);
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        var strategy = new Incremental(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 4);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 4);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, null), first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        var strategy = new Incremental(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        TimeSpan?[] expected = Seconds(1, 2, 3, null);

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
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Incremental(maxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        Assert.AreEqual("maxTries", ex.ParamName);
    }

    [TestMethod]
    public void WhenTheInitialDelayIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Incremental(3, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1)));

        Assert.AreEqual("initialDelay", ex.ParamName);
    }

    [TestMethod]
    public void WhenTheStepIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));

        Assert.AreEqual("step", ex.ParamName);
    }

    private static TimeSpan?[] Seconds(params int?[] seconds)
    {
        return Array.ConvertAll(seconds, s => s is int value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null);
    }
}
