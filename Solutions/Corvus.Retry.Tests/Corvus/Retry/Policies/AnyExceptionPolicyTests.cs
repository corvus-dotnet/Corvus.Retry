// <copyright file="AnyExceptionPolicyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Ensures that <see cref="AnyExceptionPolicy"/> attempts retries in the event of any exception.
/// </summary>
[TestClass]
public class AnyExceptionPolicyTests
{
    [TestMethod]
    [DataRow(typeof(InvalidOperationException))]
    [DataRow(typeof(ArgumentNullException))]
    [DataRow(typeof(NullReferenceException))]
    public void CanRetryReturnsTrue(Type exceptionType)
    {
        var policy = new AnyExceptionPolicy();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.IsTrue(policy.CanRetry(exception));
    }

    [TestMethod]
    public void ValidateArguments()
    {
        var policy = new AnyExceptionPolicy();

        Assert.AreEqual("exception", Assert.ThrowsExactly<ArgumentNullException>(() => policy.CanRetry(null!)).ParamName);
    }
}
