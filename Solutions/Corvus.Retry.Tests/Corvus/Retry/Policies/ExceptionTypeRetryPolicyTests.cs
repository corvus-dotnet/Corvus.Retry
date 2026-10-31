// <copyright file="ExceptionTypeRetryPolicyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="ExceptionTypeRetryPolicy"/> retries exceptions of each of its types; <see cref="ExceptionTypeRetryPolicy.Or{TException}(Func{TException, bool}?)"/>
/// adds a type in a new policy and does not change the original; one instance is safe for many threads.
/// </summary>
[TestClass]
public class ExceptionTypeRetryPolicyTests
{
    [TestMethod]
    public void OrAddsATypeToRetry()
    {
        ExceptionTypeRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>().Or<TimeoutException>();

        Assert.IsTrue(policy.CanRetry(new HttpRequestException()));
        Assert.IsTrue(policy.CanRetry(new TimeoutException()));
        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public void OrWithAPredicateRetriesOnlyTheExceptionsOfThatTypeThatThePredicateAccepts()
    {
        ExceptionTypeRetryPolicy policy = RetryPolicies.Handle<TimeoutException>().Or<IOException>(ex => ex.Message == "transient");

        Assert.IsTrue(policy.CanRetry(new TimeoutException()));
        Assert.IsTrue(policy.CanRetry(new IOException("transient")));
        Assert.IsFalse(policy.CanRetry(new IOException("permanent")));
    }

    [TestMethod]
    public void WhenTheSameTypeHasTwoPredicates_EitherPredicateCanAccept()
    {
        ExceptionTypeRetryPolicy policy = RetryPolicies.Handle<IOException>(ex => ex.Message == "a").Or<IOException>(ex => ex.Message == "b");

        Assert.IsTrue(policy.CanRetry(new IOException("a")));
        Assert.IsTrue(policy.CanRetry(new IOException("b")));
        Assert.IsFalse(policy.CanRetry(new IOException("c")));
    }

    [TestMethod]
    public void OrReturnsANewPolicyAndTheOriginalDoesNotChange()
    {
        ExceptionTypeRetryPolicy original = RetryPolicies.Handle<HttpRequestException>();

        ExceptionTypeRetryPolicy combined = original.Or<TimeoutException>();

        Assert.AreNotSame(original, combined);
        Assert.IsFalse(original.CanRetry(new TimeoutException()));
        Assert.IsTrue(combined.CanRetry(new TimeoutException()));
    }

    [TestMethod]
    public void TwoPoliciesMadeFromOneOriginalAreIndependent()
    {
        ExceptionTypeRetryPolicy original = RetryPolicies.Handle<HttpRequestException>();

        ExceptionTypeRetryPolicy withTimeout = original.Or<TimeoutException>();
        ExceptionTypeRetryPolicy withIo = original.Or<IOException>();

        Assert.IsFalse(withTimeout.CanRetry(new IOException()));
        Assert.IsFalse(withIo.CanRetry(new TimeoutException()));
    }

    [TestMethod]
    public void TheExtensionOrCombinesWithAnotherPolicy()
    {
        IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>().Or(RetryPolicies.FromPredicate(ex => ex is TimeoutException));

        Assert.IsTrue(policy.CanRetry(new HttpRequestException()));
        Assert.IsTrue(policy.CanRetry(new TimeoutException()));
        Assert.IsFalse(policy.CanRetry(new InvalidOperationException()));
    }

    [TestMethod]
    public async Task WhenManyThreadsUseOneInstance_EachGetsConsistentAnswers()
    {
        ExceptionTypeRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>().Or<TimeoutException>(ex => ex.Message == "transient");
        HttpRequestException accepted = new();
        TimeoutException rejectedByPredicate = new("permanent");
        InvalidOperationException rejected = new();

        await PolicyConcurrency.RunAsync(() =>
            policy.CanRetry(accepted) && !policy.CanRetry(rejectedByPredicate) && !policy.CanRetry(rejected)).ConfigureAwait(false);
    }
}
