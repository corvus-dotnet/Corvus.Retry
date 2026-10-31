// <copyright file="TransientHttpTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryPolicies.TransientHttp"/> accepts an <see cref="HttpRequestException"/> with no status, 408, 429
/// or 500 and more, and a <see cref="RetryTimeoutException"/>. <see cref="RetryPolicies.IsTransientHttpResponse"/>
/// gives the same results for responses.
/// </summary>
/// <remarks>
/// <para>
/// "500 or more" has no upper limit: a non-standard status of 600 or more is transient too.
/// </para>
/// <para>
/// 501 (Not Implemented) and 505 (HTTP Version Not Supported) are transient, because they are 500 or more. This is
/// a deliberate choice that the rows record. Many libraries treat them as permanent; a change to that would be a
/// breaking change, and these rows must change with it.
/// </para>
/// </remarks>
[TestClass]
public class TransientHttpTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.RequestTimeout, true)]
    [DataRow(HttpStatusCode.TooManyRequests, true)]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    [DataRow(HttpStatusCode.BadGateway, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.GatewayTimeout, true)]
    [DataRow((HttpStatusCode)599, true)]
    [DataRow((HttpStatusCode)600, true)] // "500 or more" has no upper limit (see the class remarks).
    [DataRow((HttpStatusCode)999, true)]
    [DataRow(HttpStatusCode.NotImplemented, true)] // 501 and 505 are transient by choice (see the class remarks).
    [DataRow(HttpStatusCode.HttpVersionNotSupported, true)]
    [DataRow(HttpStatusCode.OK, false)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.Conflict, false)]
    [DataRow((HttpStatusCode)499, false)]
    public void TheHttpRequestExceptionIsAcceptedOnlyForATransientStatus(HttpStatusCode status, bool expected)
    {
        Assert.AreEqual(expected, RetryPolicies.TransientHttp.CanRetry(new HttpRequestException("Failed", null, status)));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.RequestTimeout, true)]
    [DataRow(HttpStatusCode.TooManyRequests, true)]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    [DataRow(HttpStatusCode.BadGateway, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.GatewayTimeout, true)]
    [DataRow((HttpStatusCode)599, true)]
    [DataRow((HttpStatusCode)600, true)] // "500 or more" has no upper limit (see the class remarks).
    [DataRow((HttpStatusCode)999, true)]
    [DataRow(HttpStatusCode.NotImplemented, true)] // 501 and 505 are transient by choice (see the class remarks).
    [DataRow(HttpStatusCode.HttpVersionNotSupported, true)]
    [DataRow(HttpStatusCode.OK, false)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.Conflict, false)]
    [DataRow((HttpStatusCode)499, false)]
    public void IsTransientHttpResponseIsTrueOnlyForATransientStatus(HttpStatusCode status, bool expected)
    {
        using HttpResponseMessage response = new(status);

        Assert.AreEqual(expected, RetryPolicies.IsTransientHttpResponse(response));
    }

    [TestMethod]
    public void AnHttpRequestExceptionWithNoStatusIsAccepted()
    {
        Assert.IsTrue(RetryPolicies.TransientHttp.CanRetry(new HttpRequestException("No connection")));
    }

    [TestMethod]
    public void ARetryTimeoutExceptionIsAccepted()
    {
        Assert.IsTrue(RetryPolicies.TransientHttp.CanRetry(new RetryTimeoutException(TimeSpan.FromSeconds(1))));
    }

    [TestMethod]
    public void OtherExceptionsAreRefused()
    {
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new InvalidOperationException()));
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new TimeoutException()));
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new TaskCanceledException()));
    }

    [TestMethod]
    public void AnOperationCanceledExceptionFromTheCallerIsRefused()
    {
        // HttpClient reports its own timeout as a TaskCanceledException (refused above); a bare
        // OperationCanceledException, as a caller's token gives, is refused as well.
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new OperationCanceledException()));
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new OperationCanceledException(new CancellationToken(true))));
    }

    [TestMethod]
    public void ARejectionByACircuitBreakerIsRefused()
    {
        // A retry cannot pass an open or isolated circuit, so an HTTP pipeline does not retry into one.
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new BrokenCircuitException("The circuit is open.", TimeSpan.FromSeconds(5))));
        Assert.IsFalse(RetryPolicies.TransientHttp.CanRetry(new IsolatedCircuitException()));
    }

    [TestMethod]
    public void ItIsOneSharedInstance()
    {
        IRetryPolicy first = RetryPolicies.TransientHttp;
        IRetryPolicy second = RetryPolicies.TransientHttp;

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public async Task WhenManyThreadsUseIt_EachGetsConsistentAnswers()
    {
        IRetryPolicy policy = RetryPolicies.TransientHttp;
        HttpRequestException transient = new("Failed", null, HttpStatusCode.ServiceUnavailable);
        HttpRequestException permanent = new("Failed", null, HttpStatusCode.NotFound);
        RetryTimeoutException timeout = new(TimeSpan.FromSeconds(1));

        await PolicyConcurrency.RunAsync(() =>
            policy.CanRetry(transient) && !policy.CanRetry(permanent) && policy.CanRetry(timeout)).ConfigureAwait(false);
    }
}
