// <copyright file="RetryPolicyExtensionsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The extension members <c>Or</c>, <c>And</c> and <c>Not</c> give the logical result for each combination, and return
/// a new policy without a change to the original. <see cref="RetryPolicyExtensions.CanRetryAll"/> is the rule that every
/// engine (<see cref="Retriable"/>, the pipelines, hedging, the breaker, the fallback, <see cref="RetryTask"/> and
/// <see cref="ReliableTaskRunner"/>) uses to ask a policy about an <see cref="AggregateException"/>: every individual
/// failure must be acceptable.
/// </summary>
[TestClass]
public class RetryPolicyExtensionsTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void OrAcceptsAnExceptionThatEitherPolicyAccepts(bool first, bool second, bool expected)
    {
        IRetryPolicy policy = new StubRetryPolicy(first).Or(new StubRetryPolicy(second));

        Assert.AreEqual(expected, policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, true)]
    public void AndAcceptsAnExceptionThatBothPoliciesAccept(bool first, bool second, bool expected)
    {
        IRetryPolicy policy = new StubRetryPolicy(first).And(new StubRetryPolicy(second));

        Assert.AreEqual(expected, policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void NotAcceptsAnExceptionThatThePolicyRefuses(bool inner, bool expected)
    {
        IRetryPolicy policy = new StubRetryPolicy(inner).Not();

        Assert.AreEqual(expected, policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void TheExtensionMembersReturnANewPolicyAndTheOriginalDoesNotChange()
    {
        IRetryPolicy original = RetryPolicies.Handle<TimeoutException>();

        IRetryPolicy or = original.Or(RetryPolicies.Handle<InvalidOperationException>());
        IRetryPolicy and = original.And(new StubRetryPolicy(false));
        IRetryPolicy not = original.Not();

        Assert.AreNotSame(original, or);
        Assert.AreNotSame(original, and);
        Assert.AreNotSame(original, not);
        Assert.IsFalse(original.CanRetry(new InvalidOperationException()));
        Assert.IsTrue(original.CanRetry(new TimeoutException()));
        Assert.IsTrue(or.CanRetry(new InvalidOperationException()));
        Assert.IsFalse(and.CanRetry(new TimeoutException()));
        Assert.IsTrue(not.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void TheExtensionMembersCanBeChained()
    {
        IRetryPolicy policy = RetryPolicies.Handle<Exception>()
            .And(RetryPolicies.Handle<ArgumentException>().Not())
            .Or(RetryPolicies.Handle<ArgumentNullException>());

        Assert.IsTrue(policy.CanRetry(new InvalidOperationException()));
        Assert.IsFalse(policy.CanRetry(new ArgumentException()));
        Assert.IsTrue(policy.CanRetry(new ArgumentNullException()));
    }

    [TestMethod]
    public void WhenAPolicyIsNull_TheExtensionMembersThrow()
    {
        IRetryPolicy valid = new StubRetryPolicy(true);
        IRetryPolicy? missing = null;

        Assert.AreEqual("other", Assert.ThrowsExactly<ArgumentNullException>(() => valid.Or(null!)).ParamName);
        Assert.AreEqual("other", Assert.ThrowsExactly<ArgumentNullException>(() => valid.And(null!)).ParamName);
        Assert.AreEqual("policy", Assert.ThrowsExactly<ArgumentNullException>(() => missing!.Or(valid)).ParamName);
        Assert.AreEqual("policy", Assert.ThrowsExactly<ArgumentNullException>(() => missing!.And(valid)).ParamName);
        Assert.AreEqual("policy", Assert.ThrowsExactly<ArgumentNullException>(() => missing!.Not()).ParamName);
    }

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

    [TestMethod]
    public void AFailureRefusedInsideANestedAggregate_StopsTheOuterSearch()
    {
        TimeoutException first = new();
        InvalidOperationException rejected = new();
        TimeoutException nestedNeverAsked = new();
        TimeoutException outerNeverAsked = new();
        RecordingRetryPolicy policy = new(ex => ex is not InvalidOperationException);

        Assert.IsFalse(policy.CanRetryAll(new AggregateException(first, new AggregateException(rejected, nestedNeverAsked), outerNeverAsked)));

        IReadOnlyList<Exception> consulted = policy.Consulted;
        Assert.HasCount(2, consulted);
        Assert.AreSame(first, consulted[0]);
        Assert.AreSame(rejected, consulted[1]);
    }

    [TestMethod]
    public void DeeplyNestedAggregatesAreSearchedDepthFirstInOrder()
    {
        Exception a = new TimeoutException("a");
        Exception b = new TimeoutException("b");
        Exception c = new TimeoutException("c");
        Exception d = new TimeoutException("d");
        RecordingRetryPolicy policy = new(true);

        Assert.IsTrue(policy.CanRetryAll(new AggregateException(a, new AggregateException(new AggregateException(b), c), d)));

        IReadOnlyList<Exception> consulted = policy.Consulted;
        Assert.HasCount(4, consulted);
        Assert.AreSame(a, consulted[0]);
        Assert.AreSame(b, consulted[1]);
        Assert.AreSame(c, consulted[2]);
        Assert.AreSame(d, consulted[3]);
    }

    [TestMethod]
    public void AnAggregateInsideTheInnerExceptionChainOfAFailureIsNotUnwrapped()
    {
        // Only AggregateException.InnerExceptions are unwrapped. Another exception is passed as thrown, with its
        // InnerException chain; HandleInner is the policy that searches that chain.
        InvalidOperationException failure = new("outer", new AggregateException(new TimeoutException()));
        RecordingRetryPolicy policy = new(true);

        Assert.IsTrue(policy.CanRetryAll(new AggregateException(failure)));
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public void WhenThePolicyThrows_TheExceptionPropagatesUnchangedAndNoLaterFailureIsAsked()
    {
        FormatException policyFailure = new("The policy failed.");
        TimeoutException first = new();
        TimeoutException neverAsked = new();
        RecordingRetryPolicy policy = new(_ => throw policyFailure);

        FormatException thrown = Assert.ThrowsExactly<FormatException>(() => policy.CanRetryAll(new AggregateException(first, neverAsked)));

        Assert.AreSame(policyFailure, thrown);
        Assert.AreSame(first, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public void WhenThePolicyThrowsForANestedFailure_TheExceptionPropagatesUnchanged()
    {
        FormatException policyFailure = new("The policy failed.");
        TimeoutException nested = new();
        RecordingRetryPolicy policy = new(ex => ex == nested ? throw policyFailure : true);

        FormatException thrown = Assert.ThrowsExactly<FormatException>(
            () => policy.CanRetryAll(new AggregateException(new TimeoutException(), new AggregateException(nested))));

        Assert.AreSame(policyFailure, thrown);
    }
}
