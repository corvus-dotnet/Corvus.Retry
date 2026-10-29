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

        // The range goes past the attempt where the power of 2 is infinite as a double.
        for (int attempt = 1; attempt <= 1100; attempt++)
        {
            TimeSpan expected = RetryDelay.Clamp(3 * Math.Pow(2, attempt - 1), null);
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
        Assert.IsNotNull(delays[0]);
        Assert.IsNotNull(delays[1]);
        Assert.IsNull(delays[2]);
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

        TimeSpan? delay = strategy.GetRetryDelay(new RetryAttempt(2, null, null, RetryDelay.MaxSupported));

        Assert.IsNotNull(delay);
        Assert.IsInRange(TimeSpan.FromDays(40), RetryDelay.MaxSupported, delay.Value);
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
