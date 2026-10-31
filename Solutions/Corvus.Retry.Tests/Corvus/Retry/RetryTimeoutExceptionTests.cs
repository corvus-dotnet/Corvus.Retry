// <copyright file="RetryTimeoutExceptionTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryTimeoutException"/> reports the time limit that expired, and the cancellation it caused.
/// </summary>
[TestClass]
public class RetryTimeoutExceptionTests
{
    [TestMethod]
    public void ItIsATimeoutException()
    {
        Assert.IsInstanceOfType<TimeoutException>(new RetryTimeoutException(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void WhenConstructedWithATimeout_ItExposesTheTimeoutAndHasNoInnerException()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(250);

        var exception = new RetryTimeoutException(timeout);

        Assert.AreEqual(timeout, exception.Timeout);
        Assert.IsNull(exception.InnerException);
        Assert.AreEqual($"The operation did not complete within {timeout}.", exception.Message);
    }

    [TestMethod]
    public void WhenConstructedWithAnInnerException_ItExposesTheTimeoutAndTheInnerException()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        var cancellation = new OperationCanceledException();

        var exception = new RetryTimeoutException(timeout, cancellation);

        Assert.AreEqual(timeout, exception.Timeout);
        Assert.AreSame(cancellation, exception.InnerException);
        Assert.AreEqual($"The operation did not complete within {timeout}.", exception.Message);
    }

    [TestMethod]
    public void WhenConstructedWithAMessage_ItExposesTheMessageTheTimeoutAndTheInnerException()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        var cancellation = new OperationCanceledException();

        var exception = new RetryTimeoutException("Custom message", timeout, cancellation);

        Assert.AreEqual("Custom message", exception.Message);
        Assert.AreEqual(timeout, exception.Timeout);
        Assert.AreSame(cancellation, exception.InnerException);
    }
}
