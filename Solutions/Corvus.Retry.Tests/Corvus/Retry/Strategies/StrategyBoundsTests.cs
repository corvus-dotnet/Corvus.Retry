// <copyright file="StrategyBoundsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Every strategy, for every attempt number from 1 to <see cref="int.MaxValue"/> and every previous delay from zero to
/// <see cref="TimeSpan.MaxValue"/>, returns <see langword="null"/> or a delay from <see cref="TimeSpan.Zero"/> to
/// 0xFFFFFFFE milliseconds (the longest delay that a timer supports), and throws nothing. A strategy with a limit
/// returns <see langword="null"/> exactly at and after the limit.
/// </summary>
[TestClass]
public class StrategyBoundsTests
{
    // The longest delay that a timer supports, stated literally so that a change to the library's constant is detected.
    private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    private static readonly int[] AttemptNumbers = [1, 2, 31, 32, 63, 64, 1000, 100000, int.MaxValue];

    private static readonly TimeSpan[] PreviousDelays = [TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(0xFFFFFFFE), TimeSpan.MaxValue];

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Max = TimeSpan.MaxValue;

    // Each strategy by name: how to make it, the first attempt number for which it returns null (null when it has no
    // limit), and the largest delay that it may return (its maximum delay, or the cap). The name is the display name.
    private static readonly Dictionary<string, (Func<IRetryStrategy> Create, int? Limit, TimeSpan Upper)> Strategies = new()
    {
        ["new Count(32)"] = (() => new Count(32), 32, TimeSpan.Zero),
        ["new Count(int.MaxValue)"] = (() => new Count(int.MaxValue), int.MaxValue, TimeSpan.Zero),
        ["new DoNotRetry()"] = (() => new DoNotRetry(), 1, TimeSpan.Zero),

        ["new Linear(1 s, 32)"] = (() => new Linear(Second, 32), 32, Cap),
        ["new Linear(MaxValue, int.MaxValue)"] = (() => new Linear(Max, int.MaxValue), int.MaxValue, Cap),
        ["new Linear(MaxValue, int.MaxValue, max 1 h)"] = (() => new Linear(Max, int.MaxValue, Hour), int.MaxValue, Hour),
        ["new Linear(MaxValue, 64, jitter)"] = (() => new Linear(Max, 64, null, useJitter: true), 64, Cap),
        ["new Linear(MaxValue, 64, max MaxValue, jitter)"] = (() => new Linear(Max, 64, Max, useJitter: true), 64, Cap),
        ["new Linear(1 h, 64, max 1 min, jitter)"] = (() => new Linear(Hour, 64, TimeSpan.FromMinutes(1), useJitter: true), 64, TimeSpan.FromMinutes(1)),

        ["new Incremental(32, 1 s, 1 s)"] = (() => new Incremental(32, Second, Second), 32, Cap),
        ["new Incremental(int.MaxValue, MaxValue, MaxValue)"] = (() => new Incremental(int.MaxValue, Max, Max), int.MaxValue, Cap),
        ["new Incremental(int.MaxValue, 1 d, 1 d, max 1 h)"] = (() => new Incremental(int.MaxValue, Day, Day, Hour), int.MaxValue, Hour),
        ["new Incremental(int.MaxValue, MaxValue, MaxValue, jitter)"] = (() => new Incremental(int.MaxValue, Max, Max, null, useJitter: true), int.MaxValue, Cap),
        ["new Incremental(64, 1 d, 1 d, max 1 h, jitter)"] = (() => new Incremental(64, Day, Day, Hour, useJitter: true), 64, Hour),

        ["new Backoff(32, 1 s)"] = (() => new Backoff(32, Second), 32, TimeSpan.FromSeconds(30)),
        ["new Backoff(int.MaxValue, MaxValue, max MaxValue)"] = (() => new Backoff(int.MaxValue, Max) { MaxBackoff = Max }, int.MaxValue, Cap),
        ["new Backoff(int.MaxValue, 1 s, min MaxValue, max MaxValue)"] = (() => new Backoff(int.MaxValue, Second) { MinBackoff = Max, MaxBackoff = Max }, int.MaxValue, Cap),
        ["new Backoff(int.MaxValue, 0, min 0, max MaxValue)"] = (() => new Backoff(int.MaxValue, TimeSpan.Zero) { MinBackoff = TimeSpan.Zero, MaxBackoff = Max }, int.MaxValue, Cap),
        ["new Backoff(64, 1 d, max 1 h)"] = (() => new Backoff(64, Day) { MaxBackoff = Hour }, 64, Hour),

        ["new Exponential(32, 1 s)"] = (() => new Exponential(32, Second), 32, Cap),
        ["new Exponential(int.MaxValue, 1 d)"] = (() => new Exponential(int.MaxValue, Day), int.MaxValue, Cap),
        ["new Exponential(int.MaxValue, MaxValue)"] = (() => new Exponential(int.MaxValue, Max), int.MaxValue, Cap),
        ["new Exponential(int.MaxValue, 1 s, max 1 h)"] = (() => new Exponential(int.MaxValue, Second, Hour), int.MaxValue, Hour),
        ["new Exponential(int.MaxValue, 1 d, jitter)"] = (() => new Exponential(int.MaxValue, Day, null, useJitter: true), int.MaxValue, Cap),
        ["new Exponential(int.MaxValue, MaxValue, max MaxValue, jitter)"] = (() => new Exponential(int.MaxValue, Max, Max, useJitter: true), int.MaxValue, Cap),
        ["new Exponential(64, 1 s, max 1 h, jitter)"] = (() => new Exponential(64, Second, Hour, useJitter: true), 64, Hour),

        ["RetryStrategies.Constant(31, 1 s)"] = (() => RetryStrategies.Constant(31, Second), 32, Cap),
        ["RetryStrategies.Constant(int.MaxValue, MaxValue)"] = (() => RetryStrategies.Constant(int.MaxValue, Max), int.MaxValue, Cap),
        ["RetryStrategies.Constant(int.MaxValue, MaxValue, max 1 h)"] = (() => RetryStrategies.Constant(int.MaxValue, Max, Hour), int.MaxValue, Hour),
        ["RetryStrategies.Constant(63, MaxValue, jitter)"] = (() => RetryStrategies.Constant(63, Max, useJitter: true), 64, Cap),
        ["RetryStrategies.Constant(63, MaxValue, max 1 h, jitter)"] = (() => RetryStrategies.Constant(63, Max, Hour, useJitter: true), 64, Hour),

        ["RetryStrategies.Linear(31, 1 s)"] = (() => RetryStrategies.Linear(31, Second), 32, Cap),
        ["RetryStrategies.Linear(int.MaxValue, MaxValue)"] = (() => RetryStrategies.Linear(int.MaxValue, Max), int.MaxValue, Cap),
        ["RetryStrategies.Linear(int.MaxValue, 1 d, max 1 h)"] = (() => RetryStrategies.Linear(int.MaxValue, Day, Hour), int.MaxValue, Hour),
        ["RetryStrategies.Linear(int.MaxValue, MaxValue, jitter)"] = (() => RetryStrategies.Linear(int.MaxValue, Max, useJitter: true), int.MaxValue, Cap),
        ["RetryStrategies.Linear(63, 1 d, max 1 h, jitter)"] = (() => RetryStrategies.Linear(63, Day, Hour, useJitter: true), 64, Hour),

        ["RetryStrategies.Exponential(31, 1 s)"] = (() => RetryStrategies.Exponential(31, Second), 32, Cap),
        ["RetryStrategies.Exponential(int.MaxValue, MaxValue)"] = (() => RetryStrategies.Exponential(int.MaxValue, Max), int.MaxValue, Cap),
        ["RetryStrategies.Exponential(int.MaxValue, 1 s, max 1 h)"] = (() => RetryStrategies.Exponential(int.MaxValue, Second, Hour), int.MaxValue, Hour),
        ["RetryStrategies.Exponential(int.MaxValue, 1 d, jitter)"] = (() => RetryStrategies.Exponential(int.MaxValue, Day, useJitter: true), int.MaxValue, Cap),
        ["RetryStrategies.Exponential(63, 1 s, max 1 h, jitter)"] = (() => RetryStrategies.Exponential(63, Second, Hour, useJitter: true), 64, Hour),

        ["RetryStrategies.Default"] = (() => RetryStrategies.Default, 10, TimeSpan.Zero),

        ["RetryStrategies.FromDelegate(MaxValue)"] = (() => RetryStrategies.FromDelegate(_ => Max), null, Cap),
        ["RetryStrategies.FromDelegate(MinValue)"] = (() => RetryStrategies.FromDelegate(_ => TimeSpan.MinValue), null, TimeSpan.Zero),
        ["RetryStrategies.FromDelegate(PreviousDelay)"] = (() => RetryStrategies.FromDelegate(a => a.PreviousDelay), null, Cap),
        ["RetryStrategies.FromDelegate(MaxValue, null from 32)"] = (() => RetryStrategies.FromDelegate(a => a.AttemptNumber < 32 ? Max : null), 32, Cap),

        ["RetryStrategies.RetryAfter(RetryStrategies.Exponential(int.MaxValue, 1 d))"] = (() => RetryStrategies.RetryAfter(RetryStrategies.Exponential(int.MaxValue, Day)), int.MaxValue, Cap),
        ["RetryStrategies.RetryAfter(RetryStrategies.Constant(31, MaxValue))"] = (() => RetryStrategies.RetryAfter(RetryStrategies.Constant(31, Max)), 32, Cap),
        ["RetryStrategies.RetryAfter(RetryStrategies.FromDelegate(MaxValue))"] = (() => RetryStrategies.RetryAfter(RetryStrategies.FromDelegate(_ => Max)), null, Cap),
    };

    public static IEnumerable<object[]> StrategyNames => Strategies.Keys.Select(name => new object[] { name });

    [TestMethod]
    public void TheCapIsTheLongestDelayThatATimerSupports()
    {
        Assert.AreEqual(Cap, RetryDelay.MaxSupported);
    }

    [TestMethod]
    public void TheTableCoversEveryPublicStrategyClass()
    {
        Type[] strategyClasses = [.. typeof(IRetryStrategy).Assembly.GetTypes().Where(t => t.IsPublic && t.IsClass && !t.IsAbstract && typeof(IRetryStrategy).IsAssignableFrom(t))];

        Assert.IsNotEmpty(strategyClasses);
        foreach (Type type in strategyClasses)
        {
            Assert.Contains(k => k.StartsWith($"new {type.Name}(", StringComparison.Ordinal), Strategies.Keys, $"No row creates a {type.Name}.");
        }
    }

    [TestMethod]
    [DynamicData(nameof(StrategyNames))]
    public void ForEveryAttemptAndPreviousDelay_TheDelayIsNullOrFromZeroToTheCap_AndNullExactlyFromTheLimit(string name)
    {
        (Func<IRetryStrategy> create, int? limit, TimeSpan upper) = Strategies[name];
        IRetryStrategy strategy = create();

        // A strategy that jitters gives a different delay each time, so each input is asked several times.
        const int Samples = 20;
        foreach (int attemptNumber in AttemptNumbers)
        {
            foreach (TimeSpan previousDelay in PreviousDelays)
            {
                RetryAttempt attempt = new(attemptNumber, new InvalidOperationException("Failed"), null, previousDelay);
                for (int sample = 0; sample < Samples; sample++)
                {
                    TimeSpan? delay = Ask(strategy, attempt, name);
                    string input = $"{name}, attempt {attemptNumber}, previous delay {previousDelay}";

                    if (limit is int first && attemptNumber >= first)
                    {
                        Assert.IsNull(delay, $"{input}: expected no retry at or after the limit {first}.");
                        continue;
                    }

                    Assert.IsNotNull(delay, $"{input}: expected a retry before the limit.");
                    Assert.IsInRange(TimeSpan.Zero, upper, delay.Value, $"{input}: the delay is outside the range.");
                    Assert.IsInRange(TimeSpan.Zero, Cap, delay.Value, $"{input}: the delay is longer than a timer supports.");
                }
            }
        }
    }

    [TestMethod]
    [DataRow(1000)]
    [DataRow(100000)]
    [DataRow(int.MaxValue - 1)]
    public void WhenTheCalculatedDelayIsLongerThanATimerSupports_TheDelayIsTheCap(int attemptNumber)
    {
        // These strategies have no maximum delay, and their delay for these attempts overflows a TimeSpan before it is
        // limited, so the only correct answer is the cap.
        IRetryStrategy[] strategies =
        [
            new Exponential(int.MaxValue, Day),
            new Exponential(int.MaxValue, Max),
            new Incremental(int.MaxValue, Max, Max),
            new Backoff(int.MaxValue, Max) { MaxBackoff = Max },
            RetryStrategies.Linear(int.MaxValue, Max),
            RetryStrategies.Exponential(int.MaxValue, Day),
            RetryStrategies.FromDelegate(_ => Max),
        ];

        foreach (IRetryStrategy strategy in strategies)
        {
            Assert.AreEqual(Cap, strategy.GetRetryDelay(new RetryAttempt(attemptNumber, new InvalidOperationException(), null, TimeSpan.MaxValue)), strategy.GetType().Name);
        }
    }

    // Asks the strategy for the delay, and names the input in the failure if it throws.
    private static TimeSpan? Ask(IRetryStrategy strategy, in RetryAttempt attempt, string name)
    {
        TimeSpan? delay = null;
        Exception? thrown = null;
        try
        {
            delay = strategy.GetRetryDelay(attempt);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.IsNull(thrown, $"{name} threw for attempt {attempt.AttemptNumber} with previous delay {attempt.PreviousDelay}: {thrown}");
        return delay;
    }
}
