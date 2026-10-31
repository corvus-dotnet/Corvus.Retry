// <copyright file="RetryPolicyExtensionsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// <see cref="RetryPolicyExtensions.CanRetryAll"/> is the rule <see cref="RetryTask"/> and <see cref="ReliableTaskRunner"/>
    /// use to ask a policy about a faulted task: every individual failure must be acceptable.
    /// </summary>
    [TestClass]
    public class RetryPolicyExtensionsTests
    {
        [TestMethod]
        public void WhenTheAggregateHasNoInnerExceptions_ThePolicyIsAskedAboutTheAggregateItself()
        {
            AggregateException failure = new("A failure with no inner exceptions");
            RecordingRetryPolicy policy = new(false);

            Assert.IsFalse(policy.CanRetryAll(failure));
            Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
        }

        [TestMethod]
        public void NestedAggregatesAreUnwrappedButAnEmptyNestedAggregateIsTreatedAsAFailure()
        {
            TimeoutException first = new();
            InvalidOperationException second = new();
            AggregateException empty = new("A nested failure with no inner exceptions");
            AggregateException failure = new(first, new AggregateException(second, empty));
            RecordingRetryPolicy policy = new(true);

            Assert.IsTrue(policy.CanRetryAll(failure));

            IReadOnlyList<Exception> consulted = policy.Consulted;
            Assert.HasCount(3, consulted);
            Assert.AreSame(first, consulted[0]);
            Assert.AreSame(second, consulted[1]);
            Assert.AreSame(empty, consulted[2]);
        }

        [TestMethod]
        public void WhenThePolicyRejectsAnyFailure_ItStopsAskingAndReturnsFalse()
        {
            InvalidOperationException rejected = new();
            TimeoutException neverAsked = new();
            RecordingRetryPolicy policy = new(ex => ex is not InvalidOperationException);

            Assert.IsFalse(policy.CanRetryAll(new AggregateException(rejected, neverAsked)));
            Assert.AreSame(rejected, Assert.ContainsSingle(policy.Consulted));
        }
    }
}
