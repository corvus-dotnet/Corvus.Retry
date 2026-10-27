// <copyright file="AggregatePolicyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Ensures that retry is only attempted if all of the aggregated policies are satisfied.
    /// </summary>
    [TestClass]
    public class AggregatePolicyTests
    {
        [TestMethod]
        public void AllPoliciesSayNo()
        {
            var policy = new AggregatePolicy();
            policy.Policies.AddRange(MakePolicies(10, false));

            Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void AllPoliciesButOneSayNo()
        {
            var policy = new AggregatePolicy();
            policy.Policies.AddRange(MakePolicies(10, true));
            policy.Policies.AddRange(MakePolicies(1, false));

            Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void AllPoliciesSayYes()
        {
            var policy = new AggregatePolicy();
            policy.Policies.AddRange(MakePolicies(10, true));

            Assert.IsTrue(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void WhenPoliciesHasNeverBeenAccessed_ItCanRetry()
        {
            // Before 2.0 this threw a NullReferenceException, because the policy list is created on first access.
            var policy = new AggregatePolicy();

            Assert.IsTrue(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void WhenPoliciesIsEmpty_ItCanRetry()
        {
            var policy = new AggregatePolicy();
            Assert.IsEmpty(policy.Policies);

            Assert.IsTrue(policy.CanRetry(new InvalidOperationException()));
        }

        [TestMethod]
        public void ValidateArguments()
        {
            var policy = new AggregatePolicy();

            Assert.ThrowsExactly<ArgumentNullException>(() => policy.CanRetry(null!));
        }

        private static IEnumerable<IRetryPolicy> MakePolicies(int count, bool result)
        {
            return Enumerable
                .Range(0, count)
                .Select(_ => new StubRetryPolicy(result));
        }
    }
}
