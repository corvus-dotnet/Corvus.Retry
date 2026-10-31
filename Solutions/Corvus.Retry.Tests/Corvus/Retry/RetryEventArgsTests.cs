// <copyright file="RetryEventArgsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryEventArgs"/> describes a failed attempt and the retry that follows it.
/// </summary>
[TestClass]
public class RetryEventArgsTests
{
    [TestMethod]
    public void ItExposesTheValuesItWasConstructedWith()
    {
        var exception = new InvalidOperationException();
        TimeSpan delay = TimeSpan.FromMilliseconds(1234);

        var args = new RetryEventArgs(exception, null, delay, 2);

        Assert.AreSame(exception, args.LastException);
        Assert.IsNull(args.Result);
        Assert.AreEqual(delay, args.DelayBeforeRetry);
        Assert.AreEqual(2, args.AttemptNumber);
    }

    [TestMethod]
    [DataRow(1, 2)]
    [DataRow(2, 3)]
    [DataRow(9, 10)]
    public void TheNextAttemptNumberIsOneMoreThanTheAttemptNumber(int attemptNumber, int expectedNextAttemptNumber)
    {
        var args = new RetryEventArgs(new InvalidOperationException(), null, TimeSpan.Zero, attemptNumber);

        Assert.AreEqual(expectedNextAttemptNumber, args.NextAttemptNumber);
    }

    [TestMethod]
    public void WhenAResultCausedTheRetry_TheExceptionIsNullAndTheResultIsExposed()
    {
        object result = new();

        var args = new RetryEventArgs(null, result, TimeSpan.FromSeconds(1), 1);

        Assert.IsNull(args.LastException);
        Assert.AreSame(result, args.Result);
    }

    [TestMethod]
    public void TwoInstancesWithTheSameValuesAreEqual()
    {
        var exception = new InvalidOperationException();

        Assert.AreEqual(new RetryEventArgs(exception, null, TimeSpan.Zero, 1), new RetryEventArgs(exception, null, TimeSpan.Zero, 1));
        Assert.AreNotEqual(new RetryEventArgs(exception, null, TimeSpan.Zero, 1), new RetryEventArgs(exception, null, TimeSpan.Zero, 2));
    }
}
