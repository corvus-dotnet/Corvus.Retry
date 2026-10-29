// <copyright file="PolicyCombinatorTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="RetryPolicies"/> combinators: <c>HandleInner</c> finds a matching exception inside another one,
/// depth first; <c>Not</c>, <c>Any</c> and <c>All</c> give the logical result for each combination.
/// </summary>
[TestClass]
public class PolicyCombinatorTests
{
    [TestMethod]
    public void HandleInnerAcceptsTheExceptionItself()
    {
        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(new IOException()));
    }

    [TestMethod]
    public void HandleInnerAcceptsAnExceptionOfADerivedType()
    {
        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(new InvalidOperationException("outer", new FileNotFoundException())));
    }

    [TestMethod]
    public void HandleInnerAcceptsAnExceptionInTheInnerExceptionChain()
    {
        Exception failure = new InvalidOperationException("outer", new ArgumentException("middle", new IOException("inner")));

        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(failure));
    }

    [TestMethod]
    public void HandleInnerAcceptsAnExceptionInAnAggregateException()
    {
        AggregateException failure = new(new InvalidOperationException(), new IOException());

        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(failure));
    }

    [TestMethod]
    public void HandleInnerAcceptsAnExceptionInTheChainOfAnExceptionInANestedAggregateException()
    {
        AggregateException failure = new(
            new InvalidOperationException(),
            new AggregateException(new InvalidOperationException("outer", new IOException())));

        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(failure));
    }

    [TestMethod]
    public void HandleInnerRefusesWhenNoExceptionMatches()
    {
        AggregateException failure = new(new InvalidOperationException("outer", new ArgumentException()), new TimeoutException());

        Assert.IsFalse(RetryPolicies.HandleInner<IOException>().CanRetry(failure));
    }

    [TestMethod]
    public void HandleInnerWithAPredicateAcceptsOnlyAMatchThatThePredicateAccepts()
    {
        IRetryPolicy policy = RetryPolicies.HandleInner<IOException>(ex => ex.Message == "transient");

        Assert.IsTrue(policy.CanRetry(new AggregateException(new IOException("permanent"), new IOException("transient"))));
        Assert.IsFalse(policy.CanRetry(new InvalidOperationException("outer", new IOException("permanent"))));
    }

    [TestMethod]
    public void HandleInnerSearchesDepthFirst()
    {
        IOException first = new("first");
        IOException second = new("second");
        List<string> seen = [];
        IRetryPolicy policy = RetryPolicies.HandleInner<IOException>(ex =>
        {
            seen.Add(ex.Message);
            return false;
        });

        policy.CanRetry(new AggregateException(new InvalidOperationException("outer", first), second));

        Assert.AreSequenceEqual(new[] { "first", "second" }, seen);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void NotAcceptsAnExceptionThatThePolicyRefuses(bool inner, bool expected)
    {
        Assert.AreEqual(expected, RetryPolicies.Not(new StubRetryPolicy(inner)).CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void AnyAcceptsAnExceptionThatOneOrMoreOfThePoliciesAccept(bool first, bool second, bool expected)
    {
        IRetryPolicy policy = RetryPolicies.Any(new StubRetryPolicy(first), new StubRetryPolicy(second));

        Assert.AreEqual(expected, policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, true)]
    public void AllAcceptsAnExceptionThatAllOfThePoliciesAcceptLikeAggregatePolicy(bool first, bool second, bool expected)
    {
        IRetryPolicy policy = RetryPolicies.All(new StubRetryPolicy(first), new StubRetryPolicy(second));
        AggregatePolicy aggregate = new() { Policies = { new StubRetryPolicy(first), new StubRetryPolicy(second) } };
        InvalidOperationException failure = new();

        Assert.AreEqual(expected, policy.CanRetry(failure));
        Assert.AreEqual(aggregate.CanRetry(failure), policy.CanRetry(failure));
    }

    [TestMethod]
    public void AnyWithNoPoliciesRefusesAndAllWithNoPoliciesAccepts()
    {
        Assert.IsFalse(RetryPolicies.Any().CanRetry(new InvalidOperationException()));
        Assert.IsTrue(RetryPolicies.All().CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void AnyStopsAtTheFirstPolicyThatAccepts()
    {
        RecordingRetryPolicy first = new(true);
        RecordingRetryPolicy second = new(true);

        Assert.IsTrue(RetryPolicies.Any(first, second).CanRetry(new InvalidOperationException()));
        Assert.HasCount(1, first.Consulted);
        Assert.IsEmpty(second.Consulted);
    }

    [TestMethod]
    public void AllStopsAtTheFirstPolicyThatRefuses()
    {
        RecordingRetryPolicy first = new(false);
        RecordingRetryPolicy second = new(true);

        Assert.IsFalse(RetryPolicies.All(first, second).CanRetry(new InvalidOperationException()));
        Assert.HasCount(1, first.Consulted);
        Assert.IsEmpty(second.Consulted);
    }

    [TestMethod]
    public void CombinatorsCanBeNested()
    {
        // Retry an IOException, or any exception with an inner TimeoutException, but never a FileNotFoundException.
        IRetryPolicy policy = RetryPolicies.All(
            RetryPolicies.Any(RetryPolicies.Handle<IOException>(), RetryPolicies.HandleInner<TimeoutException>()),
            RetryPolicies.Not(RetryPolicies.Handle<FileNotFoundException>()));

        Assert.IsTrue(policy.CanRetry(new IOException()));
        Assert.IsTrue(policy.CanRetry(new InvalidOperationException("outer", new TimeoutException())));
        Assert.IsFalse(policy.CanRetry(new FileNotFoundException()));
        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
    }
}
