// <copyright file="RetryAfterEdgeCaseTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryStrategies.RetryAfter(IRetryStrategy, TimeProvider?)"/> with <c>Retry-After</c> values that the
/// server sends as raw text, values at the edges of what <see cref="RetryConditionHeaderValue"/> can hold, a response
/// that has been disposed, and a response that is not an error.
/// </summary>
/// <remarks>
/// The strategy reads <see cref="HttpResponseHeaders.RetryAfter"/>, so a value that the framework cannot parse has the
/// same effect as no header: the delay is the inner strategy's delay. Each such case asserts what the framework gives
/// first, so that a change in the framework's parser is reported as that, not as a change in the strategy.
/// </remarks>
[TestClass]
public class RetryAfterEdgeCaseTests
{
    // The longest delay that a timer supports.
    private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    private static readonly TimeSpan InnerDelay = TimeSpan.FromSeconds(1);

    // A whole second, because an HTTP date holds whole seconds. 1 June 2030 is a Saturday.
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider timeProvider = new(Now);

    [TestMethod]
    public void WhenTheHeaderIsARawHttpDate_TheDelayIsTheTimeUntilThatDate()
    {
        using HttpResponseMessage response = RawResponse("Sat, 01 Jun 2030 12:00:30 GMT");

        Assert.AreEqual(Now + TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Date);
        Assert.AreEqual(TimeSpan.FromSeconds(30), this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderIsARawHttpDateInThePast_TheDelayIsZero()
    {
        using HttpResponseMessage response = RawResponse("Sat, 01 Jun 2030 11:59:00 GMT");

        Assert.AreEqual(TimeSpan.Zero, this.Delay(response));
    }

    [TestMethod]
    [DataRow("soon")]
    [DataRow("")]
    [DataRow("5 seconds")]
    [DataRow("1.5")]
    [DataRow("Someday, 99 Foo 2030 25:61:61 GMT")]
    public void WhenTheHeaderIsGarbage_TheFrameworkGivesNoValueAndTheDelayIsTheInnerDelay(string value)
    {
        using HttpResponseMessage response = RawResponse(value);

        Assert.IsNull(response.Headers.RetryAfter);
        Assert.AreEqual(InnerDelay, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderIsANegativeDelta_TheFrameworkGivesNoValueAndTheDelayIsTheInnerDelay()
    {
        using HttpResponseMessage response = RawResponse("-5");

        Assert.IsNull(response.Headers.RetryAfter);
        Assert.AreEqual(InnerDelay, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDeltaDoesNotFitAnInt_TheFrameworkGivesNoValueAndTheDelayIsTheInnerDelay()
    {
        using HttpResponseMessage response = RawResponse("99999999999");

        Assert.IsNull(response.Headers.RetryAfter);
        Assert.AreEqual(InnerDelay, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDeltaIsTheLargestThatTheFrameworkParses_TheDelayIsTheCap()
    {
        // int.MaxValue seconds is about 68 years, far beyond the cap of about 49.7 days.
        using HttpResponseMessage response = RawResponse("2147483647");

        Assert.AreEqual(TimeSpan.FromSeconds(int.MaxValue), response.Headers.RetryAfter?.Delta);
        Assert.AreEqual(Cap, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDateIsDateTimeOffsetMaxValue_TheDelayIsTheCap()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.MaxValue);

        Assert.AreEqual(Cap, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderIsTheRawTextOfTheLatestHttpDate_TheDelayIsTheCap()
    {
        using HttpResponseMessage response = RawResponse("Fri, 31 Dec 9999 23:59:59 GMT");

        Assert.IsNotNull(response.Headers.RetryAfter?.Date);
        Assert.AreEqual(Cap, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDateIsDateTimeOffsetMinValue_TheDelayIsZero()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.MinValue);

        Assert.AreEqual(TimeSpan.Zero, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheResponseWasDisposedBeforeTheStrategyReadsIt_TheHeadersAreStillReadAndTheDelayIsTheDelta()
    {
        // Disposing a response disposes its content, not its headers.
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests) { Content = new StringContent("Slow down") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        response.Dispose();

        Assert.AreEqual(TimeSpan.FromSeconds(5), this.Delay(response));
    }

    [TestMethod]
    public void WhenAResponseWithARawHeaderWasDisposedBeforeTheHeaderWasParsed_TheHeaderIsStillParsedAndTheDelayIsTheDelta()
    {
        HttpResponseMessage response = RawResponse("7");
        response.Dispose();

        Assert.AreEqual(TimeSpan.FromSeconds(7), this.Delay(response));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK)]
    [DataRow(HttpStatusCode.Created)]
    [DataRow(HttpStatusCode.MovedPermanently)]
    public void WhenTheResponseIsNotAnError_TheHeaderStillGivesTheDelay(HttpStatusCode statusCode)
    {
        // The strategy does not look at the status: the result's policy decided that the response is a failure.
        using HttpResponseMessage response = new(statusCode);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TimeSpan.FromSeconds(5), this.Delay(response));
    }

    [TestMethod]
    public void WhenTheResponseIsNotAnErrorAndTheInnerStrategyRefusesARetry_ItRefusesARetry()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(0, InnerDelay), this.timeProvider);

        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(1, null, response, TimeSpan.Zero)));
    }

    private static HttpResponseMessage RawResponse(string retryAfter)
    {
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        Assert.IsTrue(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter));
        return response;
    }

    private TimeSpan? Delay(HttpResponseMessage response)
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);
        return strategy.GetRetryDelay(new RetryAttempt(1, null, response, TimeSpan.Zero));
    }
}
