// <copyright file="DoNotRetryTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="DoNotRetry"/> strategy never allows a retry.
/// </summary>
[TestClass]
public class DoNotRetryTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(100)]
    public void ItRefusesARetryAfterAnyAttempt(int attemptNumber)
    {
        Assert.IsNull(new DoNotRetry().GetRetryDelay(new RetryAttempt(attemptNumber, new InvalidOperationException(), null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void ItRefusesARetryCausedByAResult()
    {
        Assert.IsNull(new DoNotRetry().GetRetryDelay(new RetryAttempt(1, null, "result", TimeSpan.Zero)));
    }

    [TestMethod]
    public void TheSameInstanceGivesTheSameAnswersForTwoIndependentSequences()
    {
        var strategy = new DoNotRetry();

        List<TimeSpan?> first = StrategyDriver.FailTimes(strategy, 2);
        List<TimeSpan?> second = StrategyDriver.FailTimes(strategy, 2);

        Assert.AreSequenceEqual(new TimeSpan?[] { null, null }, first);
        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public async Task WhenThreadsDriveOneInstanceConcurrently_EachSeesTheDocumentedSequence()
    {
        List<List<TimeSpan?>> sequences = await StrategyDriver.FailTimesConcurrentlyAsync(new DoNotRetry(), 2).ConfigureAwait(false);

        foreach (List<TimeSpan?> sequence in sequences)
        {
            Assert.AreSequenceEqual(new TimeSpan?[] { null, null }, sequence);
        }
    }
}
