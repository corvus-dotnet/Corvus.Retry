// <copyright file="DoNotRetryTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="DoNotRetry"/> strategy never allows a retry.
/// </summary>
[TestClass]
public class DoNotRetryTests
{
    [TestMethod]
    public void CanRetryIsAlwaysFalse()
    {
        var strategy = new DoNotRetry();

        Assert.IsFalse(strategy.CanRetry);

        strategy.PrepareToRetry(new InvalidOperationException());

        Assert.IsFalse(strategy.CanRetry);
    }

    [TestMethod]
    public void PrepareToRetryReturnsZero()
    {
        Assert.AreEqual(TimeSpan.Zero, new DoNotRetry().PrepareToRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void ItDoesNotRecordExceptions()
    {
        var strategy = new DoNotRetry();

        strategy.PrepareToRetry(new InvalidOperationException());

        Assert.IsEmpty(strategy.Exception.InnerExceptions);
    }

    [TestMethod]
    public void WhenTheLastExceptionIsNull_PrepareToRetryThrows()
    {
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new DoNotRetry().PrepareToRetry(null!));

        Assert.AreEqual("lastException", ex.ParamName);
    }
}
