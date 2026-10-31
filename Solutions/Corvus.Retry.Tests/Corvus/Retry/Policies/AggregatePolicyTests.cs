// <copyright file="AggregatePolicyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void WhereverTheRefusingPolicyIs_ItRefusesAndTheLaterPoliciesAreNotAsked(int refusingIndex)
    {
        RecordingRetryPolicy[] children = [new(true), new(true), new(true)];
        children[refusingIndex] = new RecordingRetryPolicy(false);
        var policy = new AggregatePolicy();
        policy.Policies.AddRange(children);

        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));

        for (int i = 0; i < children.Length; i++)
        {
            Assert.HasCount(i <= refusingIndex ? 1 : 0, children[i].Consulted, $"Policy {i}");
        }
    }

    [TestMethod]
    public void EveryPolicyIsAskedAboutTheSameExceptionInstance()
    {
        InvalidOperationException failure = new();
        RecordingRetryPolicy first = new(true);
        RecordingRetryPolicy second = new(true);
        var policy = new AggregatePolicy { Policies = { first, second } };

        Assert.IsTrue(policy.CanRetry(failure));

        Assert.AreSame(failure, Assert.ContainsSingle(first.Consulted));
        Assert.AreSame(failure, Assert.ContainsSingle(second.Consulted));
    }

    [TestMethod]
    public void ValidateArguments()
    {
        var policy = new AggregatePolicy();

        Assert.AreEqual("exception", Assert.ThrowsExactly<ArgumentNullException>(() => policy.CanRetry(null!)).ParamName);
    }

    private static IEnumerable<IRetryPolicy> MakePolicies(int count, bool result)
    {
        return Enumerable
            .Range(0, count)
            .Select(_ => new StubRetryPolicy(result));
    }
}
