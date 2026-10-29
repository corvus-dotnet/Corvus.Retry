// <copyright file="RetryPoliciesTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryPolicies"/> creates a policy from a predicate, or from a list of exception types each with an
/// optional predicate, and validates its arguments.
/// </summary>
[TestClass]
public class RetryPoliciesTests
{
    [TestMethod]
    public void AHandlePolicyRetriesAnExceptionOfThatType()
    {
        IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>();

        Assert.IsTrue(policy.CanRetry(new HttpRequestException()));
    }

    [TestMethod]
    public void AHandlePolicyDoesNotRetryAnExceptionOfAnotherType()
    {
        IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>();

        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void AHandlePolicyRetriesAnExceptionOfADerivedType()
    {
        IRetryPolicy policy = RetryPolicies.Handle<ArgumentException>();

        Assert.IsTrue(policy.CanRetry(new ArgumentNullException("x")));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public void AHandlePolicyWithAPredicateRetriesOnlyTheExceptionsThatThePredicateAccepts(HttpStatusCode status, bool expected)
    {
        IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>(ex => ex.StatusCode == HttpStatusCode.ServiceUnavailable);

        Assert.AreEqual(expected, policy.CanRetry(new HttpRequestException(null, null, status)));
    }

    [TestMethod]
    public void AHandlePolicyWithAPredicateDoesNotCallThePredicateForAnotherType()
    {
        bool called = false;
        IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>(_ => called = true);

        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
        Assert.IsFalse(called);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void APredicatePolicyReturnsTheValueOfThePredicate(bool canRetry)
    {
        InvalidOperationException failure = new();
        Exception? seen = null;
        IRetryPolicy policy = RetryPolicies.FromPredicate(ex =>
        {
            seen = ex;
            return canRetry;
        });

        Assert.AreEqual(canRetry, policy.CanRetry(failure));
        Assert.AreSame(failure, seen);
    }

    [TestMethod]
    public void AnyAndAllCopyThePoliciesSoThatALaterChangeToTheArrayHasNoEffect()
    {
        IRetryPolicy[] policies = [new StubRetryPolicy(true)];
        IRetryPolicy any = RetryPolicies.Any(policies);
        IRetryPolicy all = RetryPolicies.All(policies);

        policies[0] = new StubRetryPolicy(false);

        Assert.IsTrue(any.CanRetry(new InvalidOperationException()));
        Assert.IsTrue(all.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void TheDefaultAnyExceptionPolicyIsOneSharedInstanceThatRetriesAnyException()
    {
        AnyExceptionPolicy first = AnyExceptionPolicy.Default;
        AnyExceptionPolicy second = AnyExceptionPolicy.Default;

        Assert.AreSame(first, second);
        Assert.IsTrue(AnyExceptionPolicy.Default.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void WhenAnArgumentIsNull_TheFactoryMethodsThrow()
    {
        Assert.AreEqual("predicate", Assert.ThrowsExactly<ArgumentNullException>(() => RetryPolicies.FromPredicate(null!)).ParamName);
        Assert.AreEqual("policy", Assert.ThrowsExactly<ArgumentNullException>(() => RetryPolicies.Not(null!)).ParamName);
        Assert.AreEqual("response", Assert.ThrowsExactly<ArgumentNullException>(() => RetryPolicies.IsTransientHttpResponse(null!)).ParamName);
    }

    [TestMethod]
    public void WhenAnyOrAllIsGivenANullPolicy_ItThrows()
    {
        IRetryPolicy valid = new StubRetryPolicy(true);

        Assert.AreEqual("policies", Assert.ThrowsExactly<ArgumentException>(() => RetryPolicies.Any(valid, null!)).ParamName);
        Assert.AreEqual("policies", Assert.ThrowsExactly<ArgumentException>(() => RetryPolicies.All(null!, valid)).ParamName);
    }

    [TestMethod]
    public void EveryPolicyRejectsANullException()
    {
        IRetryPolicy valid = new StubRetryPolicy(true);
        IRetryPolicy[] policies =
        [
            RetryPolicies.FromPredicate(_ => true),
            RetryPolicies.Handle<InvalidOperationException>(),
            RetryPolicies.HandleInner<InvalidOperationException>(),
            RetryPolicies.Not(valid),
            RetryPolicies.Any(valid),
            RetryPolicies.All(valid),
            RetryPolicies.TransientHttp,
            valid.Or(valid),
            valid.And(valid),
            valid.Not(),
            RetryPolicies.Handle<InvalidOperationException>(_ => true),
            RetryPolicies.Handle<InvalidOperationException>().Or<TimeoutException>(),
            RetryPolicies.HandleInner<InvalidOperationException>(_ => true),
            new AggregatePolicy(),
            new AggregatePolicy { Policies = { valid } },
            new AnyExceptionPolicy(),
            AnyExceptionPolicy.Default,
            new DoNotRetryPolicy(),
        ];

        foreach (IRetryPolicy policy in policies)
        {
            Assert.AreEqual("exception", Assert.ThrowsExactly<ArgumentNullException>(() => policy.CanRetry(null!)).ParamName);
        }
    }
}
