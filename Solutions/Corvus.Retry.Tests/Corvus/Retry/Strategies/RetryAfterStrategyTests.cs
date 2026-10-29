// <copyright file="RetryAfterStrategyTests.cs" company="Endjin Limited">
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
/// <see cref="RetryStrategies.RetryAfter(IRetryStrategy, TimeProvider?)"/> uses the <c>Retry-After</c> header of an
/// <see cref="HttpResponseMessage"/> result: the delta first, then the date, then the inner strategy's delay. When
/// the inner strategy refuses a retry, it refuses too.
/// </summary>
[TestClass]
public class RetryAfterStrategyTests
{
    private static readonly TimeSpan InnerDelay = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider timeProvider = new();

    [TestMethod]
    public void WhenTheHeaderHasADelta_TheDelayIsTheDelta()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(TimeSpan.FromSeconds(5), this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderTextIsANumberOfSeconds_TheDelayIsThatNumberOfSeconds()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "5");

        Assert.AreEqual(TimeSpan.FromSeconds(5), this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderHasADate_TheDelayIsTheDateMinusTheTimeFromTheTimeProvider()
    {
        DateTimeOffset date = this.timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        this.timeProvider.Advance(TimeSpan.FromSeconds(10));
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(date));

        Assert.AreEqual(TimeSpan.FromSeconds(20), this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderHasADateInThePast_TheDelayIsZero()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(this.timeProvider.GetUtcNow() - TimeSpan.FromMinutes(1)));

        Assert.AreEqual(TimeSpan.Zero, this.Delay(response));
    }

    [TestMethod]
    public void WhenNoTimeProviderIsGiven_TheSystemTimeIsUsedForADate()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay));

        TimeSpan? delay = strategy.GetRetryDelay(new RetryAttempt(1, null, response, TimeSpan.Zero));

        Assert.IsNotNull(delay);
        Assert.IsInRange(TimeSpan.FromMinutes(55), TimeSpan.FromHours(1), delay.Value);
    }

    [TestMethod]
    public void WhenTheResponseHasNoHeader_TheDelayIsTheInnerDelay()
    {
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);

        Assert.AreEqual(InnerDelay, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheResultIsNotAnHttpResponse_TheDelayIsTheInnerDelay()
    {
        Assert.AreEqual(InnerDelay, this.Delay("Not a response"));
    }

    [TestMethod]
    public void WhenAnExceptionCausedTheRetry_TheDelayIsTheInnerDelay()
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);

        Assert.AreEqual(InnerDelay, strategy.GetRetryDelay(new RetryAttempt(1, new HttpRequestException(), null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WhenTheInnerStrategyRefusesARetry_ItRefusesARetryEvenWithAHeader()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(1, InnerDelay), this.timeProvider);

        Assert.AreEqual(TimeSpan.FromSeconds(5), strategy.GetRetryDelay(new RetryAttempt(1, null, response, TimeSpan.Zero)));
        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(2, null, response, TimeSpan.FromSeconds(5))));
    }

    [TestMethod]
    public void ADeltaLongerThanATimerSupportsIsLimitedToTheLongestSupportedDelay()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(TimeSpan.FromDays(60)));

        Assert.AreEqual(RetryDelay.MaxSupported, this.Delay(response));
    }

    [TestMethod]
    public void ADateLaterThanATimerSupportsIsLimitedToTheLongestSupportedDelay()
    {
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(this.timeProvider.GetUtcNow() + TimeSpan.FromDays(60)));

        Assert.AreEqual(RetryDelay.MaxSupported, this.Delay(response));
    }

    private static HttpResponseMessage Response(RetryConditionHeaderValue retryAfter)
    {
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    private TimeSpan? Delay(object result)
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);
        return strategy.GetRetryDelay(new RetryAttempt(1, null, result, TimeSpan.Zero));
    }
}
