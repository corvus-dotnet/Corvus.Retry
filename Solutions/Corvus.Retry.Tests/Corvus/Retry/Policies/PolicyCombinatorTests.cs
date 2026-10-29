// <copyright file="PolicyCombinatorTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The <see cref="RetryPolicies"/> combinators: <c>HandleInner</c> finds a matching exception inside another one,
/// depth first; <c>Not</c>, <c>Any</c> and <c>All</c> give the logical result for each combination.
/// </summary>
[TestClass]
public class PolicyCombinatorTests
{
    // Each case calls a combinator whose child policy, predicate or delegate throws the given exception. The
    // exception is an InvalidOperationException, the type a "defensive" catch would most likely swallow.
    private static readonly Dictionary<string, Action<InvalidOperationException>> ThrowingChildCases = new()
    {
        ["Any"] = failure => RetryPolicies.Any(new StubRetryPolicy(false), Throwing(failure)).CanRetry(new TimeoutException()),
        ["All"] = failure => RetryPolicies.All(new StubRetryPolicy(true), Throwing(failure)).CanRetry(new TimeoutException()),
        ["Not"] = failure => RetryPolicies.Not(Throwing(failure)).CanRetry(new TimeoutException()),
        [".Or"] = failure => new StubRetryPolicy(false).Or(Throwing(failure)).CanRetry(new TimeoutException()),
        [".And"] = failure => new StubRetryPolicy(true).And(Throwing(failure)).CanRetry(new TimeoutException()),
        [".Not"] = failure => Throwing(failure).Not().CanRetry(new TimeoutException()),
        ["AggregatePolicy"] = failure => new AggregatePolicy { Policies = { new StubRetryPolicy(true), Throwing(failure) } }.CanRetry(new TimeoutException()),
        ["Handle<T>(predicate)"] = failure => RetryPolicies.Handle<TimeoutException>(_ => throw failure).CanRetry(new TimeoutException()),
        ["Handle<T>().Or<U>(predicate)"] = failure => RetryPolicies.Handle<IOException>().Or<TimeoutException>(_ => throw failure).CanRetry(new TimeoutException()),
        ["HandleInner<T>(predicate)"] = failure => RetryPolicies.HandleInner<TimeoutException>(_ => throw failure).CanRetry(new ArgumentException("outer", new TimeoutException())),
        ["FromDelegate strategy"] = failure => RetryStrategies.FromDelegate(_ => throw failure).GetRetryDelay(new RetryAttempt(1, new TimeoutException(), null, TimeSpan.Zero)),
        ["RetryAfter strategy"] = failure => RetryStrategies.RetryAfter(RetryStrategies.FromDelegate(_ => throw failure)).GetRetryDelay(new RetryAttempt(1, new TimeoutException(), null, TimeSpan.Zero)),
    };

    public static IEnumerable<object[]> ThrowingChildCaseNames => ThrowingChildCases.Keys.Select(key => new object[] { key });

    [TestMethod]
    [DynamicData(nameof(ThrowingChildCaseNames))]
    public void WhenAChildPolicyThrows_TheCombinatorThrowsThatExceptionUnchanged(string combinator)
    {
        InvalidOperationException failure = new("The child policy failed.");

        InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(() => ThrowingChildCases[combinator](failure));

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public void HandleInnerAcceptsAMatchInAnAggregateExceptionInsideTheInnerExceptionChain()
    {
        // The chain leads to an aggregate, whose inner exceptions are then searched.
        Exception failure = new InvalidOperationException("outer", new AggregateException(new ArgumentException(), new IOException()));

        Assert.IsTrue(RetryPolicies.HandleInner<IOException>().CanRetry(failure));
    }

    [TestMethod]
    public void HandleInnerStopsAtTheFirstMatchThatThePredicateAccepts()
    {
        IOException first = new("first");
        IOException second = new("second");
        List<Exception> seen = [];
        IRetryPolicy policy = RetryPolicies.HandleInner<IOException>(ex =>
        {
            seen.Add(ex);
            return true;
        });

        Assert.IsTrue(policy.CanRetry(new AggregateException(new InvalidOperationException("outer", first), second)));
        Assert.AreSame(first, Assert.ContainsSingle(seen));
    }

    [TestMethod]
    public void HandleWithAPredicateCallsThePredicateWithTheSameInstanceOfADerivedType()
    {
        FileNotFoundException failure = new("missing");
        List<IOException> seen = [];
        IRetryPolicy policy = RetryPolicies.Handle<IOException>(ex =>
        {
            seen.Add(ex);
            return true;
        });

        Assert.IsTrue(policy.CanRetry(failure));
        Assert.AreSame(failure, Assert.ContainsSingle(seen));
    }

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

    private static IRetryPolicy Throwing(Exception failure)
    {
        return RetryPolicies.FromPredicate(_ => throw failure);
    }
}
