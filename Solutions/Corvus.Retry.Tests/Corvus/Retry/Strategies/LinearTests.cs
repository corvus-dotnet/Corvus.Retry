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
