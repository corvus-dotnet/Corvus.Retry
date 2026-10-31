// <copyright file="RetryStrategiesTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="RetryStrategies"/> factory methods count retries, not attempts, and each gives its documented
/// formula: <c>Constant</c> gives <c>d</c>, <c>Linear</c> gives <c>d * n</c>, and <c>Exponential</c> gives
/// <c>d * 2^(n - 1)</c>.
/// </summary>
[TestClass]
public class RetryStrategiesTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    // Each key is the strategy and the parameter (after the last '.') that receives Timeout.InfiniteTimeSpan.
    private static readonly Dictionary<string, Action> InfiniteDelays = new()
    {
        ["Linear.periodicity"] = () => _ = new Linear(Timeout.InfiniteTimeSpan, 3),
        ["Linear.maxDelay"] = () => _ = new Linear(OneSecond, 3, Timeout.InfiniteTimeSpan),
        ["Incremental.initialDelay"] = () => _ = new Incremental(3, Timeout.InfiniteTimeSpan, OneSecond),
        ["Incremental.step"] = () => _ = new Incremental(3, OneSecond, Timeout.InfiniteTimeSpan),
        ["Incremental.maxDelay"] = () => _ = new Incremental(3, OneSecond, OneSecond, Timeout.InfiniteTimeSpan),
        ["Exponential.baseDelay"] = () => _ = new Exponential(3, Timeout.InfiniteTimeSpan),
        ["Exponential.maxDelay"] = () => _ = new Exponential(3, OneSecond, Timeout.InfiniteTimeSpan),
        ["Backoff.deltaBackoff"] = () => _ = new Backoff(3, Timeout.InfiniteTimeSpan),
        ["Backoff.MinBackoff"] = () => _ = new Backoff(3, OneSecond) { MinBackoff = Timeout.InfiniteTimeSpan },
        ["Backoff.MaxBackoff"] = () => _ = new Backoff(3, OneSecond) { MaxBackoff = Timeout.InfiniteTimeSpan },
        ["RetryStrategies.Constant.delay"] = () => RetryStrategies.Constant(3, Timeout.InfiniteTimeSpan),
        ["RetryStrategies.Constant.maxDelay"] = () => RetryStrategies.Constant(3, OneSecond, Timeout.InfiniteTimeSpan),
        ["RetryStrategies.Linear.delay"] = () => RetryStrategies.Linear(3, Timeout.InfiniteTimeSpan),
        ["RetryStrategies.Linear.maxDelay"] = () => RetryStrategies.Linear(3, OneSecond, Timeout.InfiniteTimeSpan),
        ["RetryStrategies.Exponential.baseDelay"] = () => RetryStrategies.Exponential(3, Timeout.InfiniteTimeSpan),
        ["RetryStrategies.Exponential.maxDelay"] = () => RetryStrategies.Exponential(3, OneSecond, Timeout.InfiniteTimeSpan),
    };

    public static IEnumerable<object[]> InfiniteDelayCases => InfiniteDelays.Keys.Select(key => new object[] { key });

    [TestMethod]
    public void ConstantWithZeroRetriesGivesOneAttempt()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Constant(0, OneSecond), 1);

        Assert.AreSequenceEqual(new TimeSpan?[] { null }, delays);
    }

    [TestMethod]
    public void ConstantWithThreeRetriesGivesFourAttemptsWithTheSameDelay()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Constant(3, OneSecond), 4);

        Assert.AreSequenceEqual(Seconds(1, 1, 1, null), delays);
    }

    [TestMethod]
    public void LinearGivesTheDelayTimesTheRetryNumber()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Linear(3, OneSecond), 4);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, null), delays);
    }

    [TestMethod]
    public void ExponentialDoublesTheDelayBeforeEachRetry()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Exponential(3, OneSecond), 4);

        Assert.AreSequenceEqual(Seconds(1, 2, 4, null), delays);
    }

    [TestMethod]
    public void LinearAndExponentialWithZeroRetriesGiveOneAttempt()
    {
        Assert.AreSequenceEqual(new TimeSpan?[] { null }, StrategyDriver.FailTimes(RetryStrategies.Linear(0, OneSecond), 1));
        Assert.AreSequenceEqual(new TimeSpan?[] { null }, StrategyDriver.FailTimes(RetryStrategies.Exponential(0, OneSecond), 1));
    }

    [TestMethod]
    public void ConstantWithJitterKeepsEachDelayWithinTwentyFivePercent()
    {
        IRetryStrategy strategy = RetryStrategies.Constant(5, OneSecond, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            foreach (TimeSpan? delay in StrategyDriver.FailTimes(strategy, 5))
            {
                Assert.IsNotNull(delay);
                Assert.IsInRange(TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250), delay.Value);
            }
        }
    }

    [TestMethod]
    public void LinearWithJitterKeepsEachDelayWithinTwentyFivePercent()
    {
        IRetryStrategy strategy = RetryStrategies.Linear(5, OneSecond, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 5);
            for (int retry = 1; retry <= delays.Count; retry++)
            {
                TimeSpan? delay = delays[retry - 1];
                Assert.IsNotNull(delay);
                Assert.IsInRange(TimeSpan.FromMilliseconds(750 * retry), TimeSpan.FromMilliseconds(1250 * retry), delay.Value);
            }
        }
    }

    [TestMethod]
    public void TheMaximumDelayAppliesToConstant()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Constant(2, OneSecond, TimeSpan.FromMilliseconds(500)), 3);

        Assert.AreSequenceEqual(new TimeSpan?[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), null }, delays);
    }

    [TestMethod]
    public void TheMaximumDelayAppliesToLinear()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Linear(4, OneSecond, TimeSpan.FromMilliseconds(2500)), 5);

        Assert.AreSequenceEqual(
            new TimeSpan?[] { OneSecond, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(2500), null },
            delays);
    }

    [TestMethod]
    public void TheMaximumDelayAppliesToExponential()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Exponential(4, OneSecond, TimeSpan.FromSeconds(3)), 5);

        Assert.AreSequenceEqual(Seconds(1, 2, 3, 3, null), delays);
    }

    [TestMethod]
    public void ConstantWithJitter_ManyDelaysForTheSameAttemptAreSpreadAcrossTheBand()
    {
        IRetryStrategy strategy = RetryStrategies.Constant(1, OneSecond, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250));
    }

    [TestMethod]
    public void LinearWithJitter_ManyDelaysForTheSameAttemptAreSpreadAcrossTheBand()
    {
        // Before retry 2 the delay without jitter is 2 s, so the band is [1.5 s, 2.5 s].
        IRetryStrategy strategy = RetryStrategies.Linear(2, OneSecond, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(2, new InvalidOperationException(), null, OneSecond));

        JitterSamples.AssertSpreadAcrossTheBand(delays, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500));
    }

    [TestMethod]
    public void TheMaximumDelayAppliesAfterTheJitterToConstant()
    {
        // The band [750 ms, 1250 ms] is cut at 900 ms.
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(900);
        IRetryStrategy strategy = RetryStrategies.Constant(5, OneSecond, maxDelay, useJitter: true);

        List<TimeSpan> delays = JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero));

        JitterSamples.AssertLimitedByTheMaximum(delays, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250), maxDelay);
    }

    [TestMethod]
    public void TheMaximumDelayAppliesAfterTheJitterToLinear()
    {
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(900);
        IRetryStrategy strategy = RetryStrategies.Linear(5, OneSecond, maxDelay, useJitter: true);

        // Retry 1: the band [750 ms, 1250 ms] is cut at 900 ms.
        JitterSamples.AssertLimitedByTheMaximum(
            JitterSamples.Take(strategy, new RetryAttempt(1, new InvalidOperationException(), null, TimeSpan.Zero)),
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromMilliseconds(1250),
            maxDelay);

        // Retries 2 to 5: the whole band lies above the maximum.
        for (int attempt = 2; attempt <= 5; attempt++)
        {
            foreach (TimeSpan delay in JitterSamples.Take(strategy, new RetryAttempt(attempt, new InvalidOperationException(), null, maxDelay)))
            {
                Assert.AreEqual(maxDelay, delay);
            }
        }
    }

    [TestMethod]
    public void TheMaximumDelayAppliesAfterTheJitterToExponential()
    {
        // Every jittered delay is at least the base delay, which is longer than the maximum.
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(900);
        IRetryStrategy strategy = RetryStrategies.Exponential(5, OneSecond, maxDelay, useJitter: true);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            Assert.AreSequenceEqual(new TimeSpan?[] { maxDelay, maxDelay, maxDelay, maxDelay, maxDelay, null }, StrategyDriver.FailTimes(strategy, 6));
        }
    }

    [TestMethod]
    public void IntMaxValueRetriesDoesNotOverflow()
    {
        IRetryStrategy strategy = RetryStrategies.Constant(int.MaxValue, OneSecond);

        Assert.AreEqual(OneSecond, strategy.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, null, null, OneSecond)));
        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(int.MaxValue, null, null, OneSecond)));
    }

    [TestMethod]
    public void IntMaxValueRetriesDoesNotOverflowForLinearOrExponential()
    {
        // Both share the conversion from retries to tries with Constant. Their delays at this attempt are limited
        // to the longest supported delay.
        IRetryStrategy linear = RetryStrategies.Linear(int.MaxValue, OneSecond);
        IRetryStrategy exponential = RetryStrategies.Exponential(int.MaxValue, OneSecond);

        Assert.AreEqual(RetryDelay.MaxSupported, linear.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, null, null, OneSecond)));
        Assert.IsNull(linear.GetRetryDelay(new RetryAttempt(int.MaxValue, null, null, OneSecond)));
        Assert.AreEqual(RetryDelay.MaxSupported, exponential.GetRetryDelay(new RetryAttempt(int.MaxValue - 1, null, null, OneSecond)));
        Assert.IsNull(exponential.GetRetryDelay(new RetryAttempt(int.MaxValue, null, null, OneSecond)));
    }

    [TestMethod]
    public void OneRetryGivesTwoAttempts()
    {
        Assert.AreSequenceEqual(Seconds(1, null), StrategyDriver.FailTimes(RetryStrategies.Constant(1, OneSecond), 2));
        Assert.AreSequenceEqual(Seconds(1, null), StrategyDriver.FailTimes(RetryStrategies.Linear(1, OneSecond), 2));
        Assert.AreSequenceEqual(Seconds(1, null), StrategyDriver.FailTimes(RetryStrategies.Exponential(1, OneSecond), 2));
    }

    [TestMethod]
    public void FromDelegateReturnsTheDelegateValueAndANullValueStopsTheRetries()
    {
        List<RetryAttempt> seen = [];
        List<Exception> thrown = [];
        IRetryStrategy strategy = RetryStrategies.FromDelegate(attempt =>
        {
            seen.Add(attempt);
            return attempt.AttemptNumber < 3 ? TimeSpan.FromSeconds(attempt.AttemptNumber * 10) : null;
        });

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 3, attempt =>
        {
            InvalidOperationException failure = new($"Failure {attempt}");
            thrown.Add(failure);
            return failure;
        });

        Assert.AreSequenceEqual(Seconds(10, 20, null), delays);
        Assert.HasCount(3, seen);
        Assert.AreEqual(new RetryAttempt(1, thrown[0], null, TimeSpan.Zero), seen[0]);
        Assert.AreEqual(new RetryAttempt(2, thrown[1], null, TimeSpan.FromSeconds(10)), seen[1]);
        Assert.AreEqual(new RetryAttempt(3, thrown[2], null, TimeSpan.FromSeconds(20)), seen[2]);
        Assert.AreSame(thrown[2], seen[2].Exception);
    }

    [TestMethod]
    public void FromDelegateLimitsTheDelegateValueToTheSupportedRange()
    {
        Assert.AreEqual(RetryDelay.MaxSupported, RetryStrategies.FromDelegate(_ => TimeSpan.MaxValue).GetRetryDelay(new RetryAttempt(1, null, null, TimeSpan.Zero)));
        Assert.AreEqual(TimeSpan.Zero, RetryStrategies.FromDelegate(_ => TimeSpan.FromSeconds(-1)).GetRetryDelay(new RetryAttempt(1, null, null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void DefaultGivesTenAttemptsWithoutDelayAndIsTheSameInstanceOnEachRead()
    {
        List<TimeSpan?> delays = StrategyDriver.FailTimes(RetryStrategies.Default, 10);

        Assert.AreSequenceEqual(
            new TimeSpan?[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null },
            delays);
        IRetryStrategy first = RetryStrategies.Default;
        IRetryStrategy second = RetryStrategies.Default;
        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void WhenRetriesIsNegative_TheFactoryMethodsThrow()
    {
        Assert.AreEqual("retries", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Constant(-1, OneSecond)).ParamName);
        Assert.AreEqual("retries", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Linear(-1, OneSecond)).ParamName);
        Assert.AreEqual("retries", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Exponential(-1, OneSecond)).ParamName);
    }

    [TestMethod]
    public void WhenADelayIsNegative_TheFactoryMethodsThrow()
    {
        TimeSpan negative = TimeSpan.FromSeconds(-1);

        Assert.AreEqual("delay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Constant(1, negative)).ParamName);
        Assert.AreEqual("delay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Linear(1, negative)).ParamName);
        Assert.AreEqual("baseDelay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Exponential(1, negative)).ParamName);
        Assert.AreEqual("maxDelay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Constant(1, OneSecond, negative)).ParamName);
        Assert.AreEqual("maxDelay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Linear(1, OneSecond, negative)).ParamName);
        Assert.AreEqual("maxDelay", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetryStrategies.Exponential(1, OneSecond, negative)).ParamName);
    }

    [TestMethod]
    [DynamicData(nameof(InfiniteDelayCases))]
    public void WhenADelayParameterIsInfinite_ItIsRejectedWithTheParameterName(string parameter)
    {
        // Timeout.InfiniteTimeSpan (-1 ms) is the most likely way to write "no limit"; it is negative, so it is
        // rejected rather than taken as no limit. Use null for no maximum delay.
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(InfiniteDelays[parameter]);

        Assert.AreEqual(parameter[(parameter.LastIndexOf('.') + 1)..], ex.ParamName);
    }

    [TestMethod]
    public void WhenADelegateOrInnerStrategyIsNull_TheFactoryMethodsThrow()
    {
        Assert.AreEqual("getRetryDelay", Assert.ThrowsExactly<ArgumentNullException>(() => RetryStrategies.FromDelegate(null!)).ParamName);
        Assert.AreEqual("inner", Assert.ThrowsExactly<ArgumentNullException>(() => RetryStrategies.RetryAfter(null!)).ParamName);
    }

    private static TimeSpan?[] Seconds(params int?[] seconds)
    {
        return Array.ConvertAll(seconds, s => s is int value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null);
    }
}
