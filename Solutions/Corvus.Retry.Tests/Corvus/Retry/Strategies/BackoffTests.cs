// <copyright file="BackoffTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="Backoff"/> strategy increases the delay exponentially, with jitter, between bounds.
/// </summary>
/// <remarks>
/// After failed attempt n the delay is
/// <c>min(MinBackoff + (2^n - 1) * r, MaxBackoff)</c>, where r is a random time
/// in <c>[0.8 * DeltaBackoff, 1.2 * DeltaBackoff)</c>. The bounds below are therefore deterministic.
/// </remarks>
[TestClass]
public class BackoffTests
{
    private const int Iterations = 20;

    // With MinBackoff 1000ms and DeltaBackoff 1000ms, r lies in [800, 1200).
    private const int MinMs = 1000;
    private const int LowJitterMs = 800;
    private const int HighJitterMs = 1200;

    [TestMethod]
    public void EachRetryDelayLiesWithinTheDocumentedJitterBand()
    {
        // n=1: [1800, 2200], n=2: [3400, 4600], n=3: [6600, 9400] ms. Integer arithmetic keeps the bounds exact.
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            var strategy = new Backoff(4, TimeSpan.FromMilliseconds(1000));

            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 3);

            AssertWithinJitterBand(delays);
        }
    }

    [TestMethod]
    public void EachRetryWaitsLongerThanThePrevious()
    {
        // Deterministic despite the jitter: (2^n - 1) * 1.2 < (2^(n+1) - 1) * 0.8 for every n >= 1.
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            var strategy = new Backoff(5, TimeSpan.FromSeconds(1));

            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 4);

            for (int i = 1; i < delays.Count; i++)
            {
                Assert.IsGreaterThan(delays[i - 1]!.Value, delays[i]!.Value);
            }
        }
    }

    [TestMethod]
    public void WhenTheDeltaIsZero_EveryRetryWaitsExactlyMinBackoff()
    {
        var strategy = new Backoff(4, TimeSpan.Zero);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { strategy.MinBackoff, strategy.MinBackoff, strategy.MinBackoff }, delays);
        Assert.AreEqual(TimeSpan.FromSeconds(1), strategy.MinBackoff);
    }

    [TestMethod]
    public void TheDelayNeverExceedsMaxBackoff()
    {
        TimeSpan max = TimeSpan.FromSeconds(2);
        var strategy = new Backoff(5, TimeSpan.FromSeconds(1)) { MaxBackoff = max };

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 4);

        foreach (TimeSpan? delay in delays)
        {
            Assert.IsLessThanOrEqualTo(max, delay!.Value);
        }

        // From the second retry onwards the uncapped delay is at least 1s + 3 * 0.8s, so the cap applies.
        Assert.AreSequenceEqual(new TimeSpan?[] { max, max, max }, delays.GetRange(1, 3));
    }

    [TestMethod]
    public void RaisingMinBackoffRaisesEveryDelay()
    {
        TimeSpan min = TimeSpan.FromSeconds(5);
        var strategy = new Backoff(3, TimeSpan.Zero) { MinBackoff = min };

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 2);

        Assert.AreSequenceEqual(new TimeSpan?[] { min, min }, delays);
    }

    [TestMethod]
    public void WhenConstructedWithoutArguments_ItAllowsFiveTriesWithATwoSecondDeltaAndDefaultBounds()
    {
        var strategy = new Backoff();

        Assert.AreEqual(TimeSpan.FromSeconds(2), strategy.DeltaBackoff);
        Assert.AreEqual(TimeSpan.FromSeconds(1), strategy.MinBackoff);
        Assert.AreEqual(TimeSpan.FromSeconds(30), strategy.MaxBackoff);
        Assert.AreEqual(strategy.DefaultMinBackoff, strategy.MinBackoff);
        Assert.AreEqual(strategy.DefaultMaxBackoff, strategy.MaxBackoff);

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 5);

        for (int i = 0; i < 4; i++)
        {
            Assert.IsNotNull(delays[i]);
        }

        Assert.IsNull(delays[4]);
    }

    [TestMethod]
    public void WhenNoRetriesRemain_ItRefusesARetry()
    {
        var strategy = new Backoff(2, TimeSpan.FromSeconds(1));

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 2);

        Assert.IsNotNull(delays[0]);
        Assert.IsNull(delays[1]);
    }

    [TestMethod]
    public void ADelayLongerThanATimerSupportsIsLimitedToTheLongestSupportedDelay()
    {
        var strategy = new Backoff(int.MaxValue, TimeSpan.FromDays(1)) { MaxBackoff = TimeSpan.MaxValue };

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 100);

        Assert.AreEqual(RetryDelay.MaxSupported, delays[99]);
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        // The delays are random, so each sequence is checked against the documented band rather than the other.
        var strategy = new Backoff(4, TimeSpan.FromMilliseconds(1000));

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 4);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 4);

        AssertWithinJitterBand(first);
        AssertWithinJitterBand(second);
        Assert.IsNull(first[3]);
        Assert.IsNull(second[3]);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        var strategy = new Backoff(4, TimeSpan.FromMilliseconds(1000));

        List<List<TimeSpan?>> sequences = await StrategyDriver.FailTimesConcurrentlyAsync(strategy, 4).ConfigureAwait(false);

        foreach (List<TimeSpan?> sequence in sequences)
        {
            AssertWithinJitterBand(sequence);
            Assert.IsNull(sequence[3]);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void WhenTheDeltaIsAFewMilliseconds_TheJitterStaysWithinTwentyPercent(int deltaMilliseconds)
    {
        // Before 3.0 the jitter was a whole number of milliseconds, so a delta of 1 ms always gave 0 (-100%), and a
        // delta of 4 ms always gave 3 ms.
        TimeSpan delta = TimeSpan.FromMilliseconds(deltaMilliseconds);
        var strategy = new Backoff(2, delta) { MinBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.MaxValue };

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        // With a zero MinBackoff, the first delay is r itself.
        JitterSamples.AssertSpreadAcrossTheBand(delays, delta * 0.8, delta * 1.2);
    }

    [TestMethod]
    public void WhenTheDeltaIsLongerThanInt32MaxValueMilliseconds_TheJitterStaysWithinTwentyPercent()
    {
        // Before 3.0 both bounds of the jitter saturated at int.MaxValue ms (about 24.86 days), below the band.
        var strategy = new Backoff(3, TimeSpan.FromDays(40)) { MinBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.MaxValue };

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        // The band [32 d, 48 d] lies below the longest supported delay (about 49.7 days), so the cap does not apply.
        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromDays(32), TimeSpan.FromDays(48));
    }

    [TestMethod]
    [DataRow(-1)] // Timeout.InfiniteTimeSpan is -1 ms, so it is rejected too.
    [DataRow(-100_000)]
    public void WhenMinBackoffIsNegative_TheInitialiserThrows(int milliseconds)
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new Backoff(3, TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.FromMilliseconds(milliseconds) });

        Assert.AreEqual("MinBackoff", ex.ParamName);
    }

    [TestMethod]
    [DataRow(-1)] // Timeout.InfiniteTimeSpan is -1 ms, so it is rejected too.
    [DataRow(-100_000)]
    public void WhenMaxBackoffIsNegative_TheInitialiserThrows(int milliseconds)
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new Backoff(3, TimeSpan.FromSeconds(1)) { MaxBackoff = TimeSpan.FromMilliseconds(milliseconds) });

        Assert.AreEqual("MaxBackoff", ex.ParamName);
    }

    [TestMethod]
    public void WhenMinBackoffIsGreaterThanMaxBackoff_GetRetryDelayThrows()
    {
        // The initialiser cannot check the pair, because the caller sets the bounds in any order (see
        // TheBoundsCanBeSetInEitherOrder), so the first call checks it.
        var strategy = new Backoff(3, TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(2) };

        ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(
            () => strategy.GetRetryDelay(new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)));

        Assert.AreEqual("MinBackoff", ex.ParamName);
    }

    [TestMethod]
    public void WhenOnlyMinBackoffIsSetAboveTheDefaultMaxBackoff_GetRetryDelayThrows()
    {
        var strategy = new Backoff(3, TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.FromSeconds(60) };

        ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(
            () => strategy.GetRetryDelay(new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)));

        Assert.AreEqual("MinBackoff", ex.ParamName);
    }

    [TestMethod]
    public void WhenMinBackoffIsGreaterThanMaxBackoff_GetRetryDelayThrowsEvenWhenNoRetriesRemain()
    {
        var strategy = new Backoff(1, TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(2) };

        ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(
            () => strategy.GetRetryDelay(new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)));

        Assert.AreEqual("MinBackoff", ex.ParamName);
    }

    [TestMethod]
    public void TheBoundsCanBeSetInEitherOrder()
    {
        // Each initialiser on its own moves MinBackoff above the default MaxBackoff of 30 s.
        TimeSpan min = TimeSpan.FromSeconds(60);
        TimeSpan max = TimeSpan.FromSeconds(120);
        var minFirst = new Backoff(2, TimeSpan.Zero) { MinBackoff = min, MaxBackoff = max };
        var maxFirst = new Backoff(2, TimeSpan.Zero) { MaxBackoff = max, MinBackoff = min };

        Assert.AreSequenceEqual(new TimeSpan?[] { min, null }, StrategyDriver.FailTimes(minFirst, 2));
        Assert.AreSequenceEqual(new TimeSpan?[] { min, null }, StrategyDriver.FailTimes(maxFirst, 2));
    }

    [TestMethod]
    public void WhenMinBackoffEqualsMaxBackoff_EveryDelayIsThatValue()
    {
        TimeSpan bound = TimeSpan.FromSeconds(5);
        var strategy = new Backoff(4, TimeSpan.FromSeconds(1)) { MinBackoff = bound, MaxBackoff = bound };

        Assert.AreSequenceEqual(new TimeSpan?[] { bound, bound, bound, null }, StrategyDriver.FailTimes(strategy, 4));
    }

    [TestMethod]
    public void ZeroBoundsAreAccepted()
    {
        var strategy = new Backoff(2, TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero };

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.Zero, null }, StrategyDriver.FailTimes(strategy, 2));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void WhenMaxTriesIsNotPositive_TheConstructorThrows(int maxTries)
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Backoff(maxTries, TimeSpan.FromSeconds(1)));

        Assert.AreEqual("maxTries", ex.ParamName);
    }

    [TestMethod]
    public void WhenTheDeltaIsNegative_TheConstructorThrows()
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Backoff(3, TimeSpan.FromSeconds(-1)));

        Assert.AreEqual("deltaBackoff", ex.ParamName);
    }

    // Checks the first three delays of a sequence from a Backoff with MinBackoff 1000ms and DeltaBackoff 1000ms.
    private static void AssertWithinJitterBand(List<TimeSpan?> delays)
    {
        for (int n = 1; n <= 3; n++)
        {
            int factor = (1 << n) - 1;
            TimeSpan lower = TimeSpan.FromMilliseconds(MinMs + (factor * LowJitterMs));
            TimeSpan upper = TimeSpan.FromMilliseconds(MinMs + (factor * HighJitterMs));
            Assert.IsInRange(lower, upper, delays[n - 1]!.Value);
        }
    }
}
