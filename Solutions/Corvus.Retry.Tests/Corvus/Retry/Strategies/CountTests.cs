// <copyright file="CountTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="Count"/> strategy allows a fixed number of tries with no delay between them.
/// </summary>
[TestClass]
public class CountTests
{
    [TestMethod]
    public void WhenConstructedWithoutArguments_ItAllowsFiveTries()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Count(), 5);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null }, delays);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(10)]
    public void WhenConstructedWithMaxTries_ItRefusesARetryAfterThatManyFailedAttempts(int maxTries)
    {
        var strategy = new Count(maxTries);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, maxTries);

        Assert.IsNull(delays[maxTries - 1]);
        for (int i = 0; i < maxTries - 1; i++)
        {
            Assert.AreEqual(TimeSpan.Zero, delays[i]);
        }
    }

    [TestMethod]
    public void WhenMaxTriesIsOne_TheFirstFailedAttemptIsNotRetried()
    {
        Assert.IsNull(new Count(1).GetRetryDelay(new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void IntMaxValueTriesDoesNotOverflow()
    {
        var strategy = new Count(int.MaxValue);

        Assert.AreEqual(TimeSpan.Zero, strategy.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, new InvalidOperationException(), null, TimeSpan.Zero)));
        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(int.MaxValue, new InvalidOperationException(), null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void ItNeverDelaysBetweenRetries()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(new Count(4), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }, delays);
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        var strategy = new Count(3);

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 3);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.Zero, TimeSpan.Zero, null }, first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        var strategy = new Count(3);
        TimeSpan?[] expected = [TimeSpan.Zero, TimeSpan.Zero, null];

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
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Count(maxTries));

        Assert.AreEqual("maxTries", ex.ParamName);
    }
}
