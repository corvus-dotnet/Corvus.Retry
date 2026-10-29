// <copyright file="LinearTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
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
        var strategy = new Linear(periodicity, 4);

        // Only the delays before actual retries are asserted. After the final failure Linear still returns the
        // periodicity, but no caller sleeps on it because CanRetry is false by then.
        var delays = new List<TimeSpan>();
        for (int i = 0; i < 3; i++)
        {
            delays.Add(strategy.PrepareToRetry(new InvalidOperationException()));
        }

        Assert.AreSequenceEqual(new[] { periodicity, periodicity, periodicity }, delays);
    }

    [TestMethod]
    public void CanRetryBecomesFalseAfterMaxTriesFailures()
    {
        var strategy = new Linear(TimeSpan.FromSeconds(1), 3);

        Assert.IsTrue(strategy.CanRetry);
        strategy.PrepareToRetry(new InvalidOperationException());
        strategy.PrepareToRetry(new InvalidOperationException());
        Assert.IsTrue(strategy.CanRetry);

        strategy.PrepareToRetry(new InvalidOperationException());
        Assert.IsFalse(strategy.CanRetry);
    }

    [TestMethod]
    public void ItRecordsEveryFailureInOrder()
    {
        var strategy = new Linear(TimeSpan.FromSeconds(1), 3);
        Exception[] failures = [new InvalidOperationException("1"), new TimeoutException("2"), new InvalidOperationException("3")];

        foreach (Exception failure in failures)
        {
            strategy.PrepareToRetry(failure);
        }

        IReadOnlyList<Exception> recorded = strategy.Exception.InnerExceptions;
        Assert.HasCount(3, recorded);
        for (int i = 0; i < failures.Length; i++)
        {
            Assert.AreSame(failures[i], recorded[i]);
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
    public void WhenTheLastExceptionIsNull_PrepareToRetryThrows()
    {
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new Linear(TimeSpan.FromSeconds(1), 3).PrepareToRetry(null!));

        Assert.AreEqual("lastException", ex.ParamName);
    }
}
