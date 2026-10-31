// <copyright file="DoNotRetryPolicyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Ensures that <see cref="DoNotRetryPolicy"/> never attempts retries.
    /// </summary>
    [TestClass]
    public class DoNotRetryPolicyTests
    {
        [TestMethod]
        public void CanRetryReturnsFalse()
        {
            var policy = new DoNotRetryPolicy();

            Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void ValidateArguments()
        {
            var policy = new DoNotRetryPolicy();

            Assert.ThrowsExactly<ArgumentNullException>(() => policy.CanRetry(null!));
        }
    }
}
