// <copyright file="RetryAttemptTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="RetryAttempt"/> numbers attempts from 1. An attempt number less than 1 is rejected when the attempt
/// is created, so that no strategy receives one.
/// </summary>
[TestClass]
public class RetryAttemptTests
{
    private static readonly Dictionary<string, IRetryStrategy> Strategies = new()
    {
        ["Count"] = new Count(3),
        ["Linear"] = new Linear(TimeSpan.FromSeconds(1), 3),
        ["Incremental"] = new Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
        ["Exponential"] = new Exponential(3, TimeSpan.FromSeconds(1)),
        ["Backoff"] = new Backoff(3, TimeSpan.FromSeconds(1)),
        ["DoNotRetry"] = new DoNotRetry(),
        ["RetryStrategies.Constant"] = RetryStrategies.Constant(2, TimeSpan.FromSeconds(1)),
        ["RetryStrategies.Linear"] = RetryStrategies.Linear(2, TimeSpan.FromSeconds(1)),
        ["RetryStrategies.Exponential"] = RetryStrategies.Exponential(2, TimeSpan.FromSeconds(1)),
        ["RetryStrategies.FromDelegate"] = RetryStrategies.FromDelegate(_ => TimeSpan.FromSeconds(1)),
        ["RetryStrategies.RetryAfter"] = RetryStrategies.RetryAfter(RetryStrategies.Constant(2, TimeSpan.FromSeconds(1))),
    };

    public static IEnumerable<object[]> InvalidAttemptForEveryStrategy
    {
        get
        {
            foreach (string strategy in Strategies.Keys)
            {
                yield return [strategy, 0];
                yield return [strategy, -3];
            }
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    [DataRow(int.MinValue)]
    public void WhenTheAttemptNumberIsLessThanOne_TheConstructorThrows(int attemptNumber)
    {
        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RetryAttempt(attemptNumber, new InvalidOperationException(), null, TimeSpan.Zero));

        Assert.AreEqual("AttemptNumber", ex.ParamName);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public void WhenAWithExpressionSetsAnAttemptNumberLessThanOne_ItThrows(int attemptNumber)
    {
        RetryAttempt valid = new(1, new InvalidOperationException(), null, TimeSpan.Zero);

        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => valid with { AttemptNumber = attemptNumber });

        Assert.AreEqual("AttemptNumber", ex.ParamName);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(int.MaxValue)]
    public void AnAttemptNumberOfOneOrMoreIsKept(int attemptNumber)
    {
        InvalidOperationException failure = new();
        RetryAttempt attempt = new(attemptNumber, failure, "result", TimeSpan.FromSeconds(3));

        Assert.AreEqual(attemptNumber, attempt.AttemptNumber);
        Assert.AreSame(failure, attempt.Exception);
        Assert.AreEqual("result", attempt.Result);
        Assert.AreEqual(TimeSpan.FromSeconds(3), attempt.PreviousDelay);
        Assert.AreEqual(attemptNumber, (attempt with { PreviousDelay = TimeSpan.Zero }).AttemptNumber);
    }

    [TestMethod]
    [DynamicData(nameof(InvalidAttemptForEveryStrategy))]
    public void NoStrategyCanBeAskedAboutAnAttemptNumberLessThanOne(string strategyName, int attemptNumber)
    {
        // The check is in RetryAttempt, so it applies to every strategy, including a custom one. This matrix records
        // that no strategy gives a delay for attempt 0 or a negative attempt (before 3.0, Exponential gave 0.5 s for
        // attempt 0, and Count, Incremental and Backoff gave other values).
        IRetryStrategy strategy = Strategies[strategyName];

        ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => strategy.GetRetryDelay(new RetryAttempt(attemptNumber, new InvalidOperationException(), null, TimeSpan.Zero)));

        Assert.AreEqual("AttemptNumber", ex.ParamName);
    }
}
