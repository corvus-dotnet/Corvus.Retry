// <copyright file="RetryStrategiesTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
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
    public void TheMaximumDelayAppliesAfterTheJitter()
    {
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(900);
        IRetryStrategy[] strategies =
        [
            RetryStrategies.Constant(5, OneSecond, maxDelay, useJitter: true),
            RetryStrategies.Linear(5, OneSecond, maxDelay, useJitter: true),
            RetryStrategies.Exponential(5, OneSecond, maxDelay, useJitter: true),
        ];

        foreach (IRetryStrategy strategy in strategies)
        {
            for (int iteration = 0; iteration < 50; iteration++)
            {
                foreach (TimeSpan? delay in StrategyDriver.FailTimes(strategy, 5))
                {
                    Assert.IsNotNull(delay);
                    Assert.IsLessThanOrEqualTo(maxDelay, delay.Value);
                }
            }
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
    public void FromDelegateReturnsTheDelegateValueAndANullValueStopsTheRetries()
    {
        List<RetryAttempt> seen = [];
        IRetryStrategy strategy = RetryStrategies.FromDelegate(attempt =>
        {
            seen.Add(attempt);
            return attempt.AttemptNumber < 3 ? TimeSpan.FromSeconds(attempt.AttemptNumber * 10) : null;
        });

        List<TimeSpan?> delays = StrategyDriver.FailTimes(strategy, 3);

        Assert.AreSequenceEqual(Seconds(10, 20, null), delays);
        Assert.HasCount(3, seen);
        Assert.AreEqual(1, seen[0].AttemptNumber);
        Assert.AreEqual(TimeSpan.Zero, seen[0].PreviousDelay);
        Assert.AreEqual(2, seen[1].AttemptNumber);
        Assert.AreEqual(TimeSpan.FromSeconds(10), seen[1].PreviousDelay);
        Assert.IsInstanceOfType<InvalidOperationException>(seen[2].Exception);
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
