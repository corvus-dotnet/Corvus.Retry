// <copyright file="RetryAfterStrategyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
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
        // Without reading the clock: the system time is after 2010 and long before the year 9000. A FakeTimeProvider
        // (which starts at 2000-01-01) would give a long delay for the first date, and a minimum or zero time would
        // give one too.
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay));
        using HttpResponseMessage past = Response(new RetryConditionHeaderValue(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        using HttpResponseMessage future = Response(new RetryConditionHeaderValue(new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.AreEqual(TimeSpan.Zero, strategy.GetRetryDelay(new RetryAttempt(1, null, past, TimeSpan.Zero)));
        Assert.AreEqual(RetryDelay.MaxSupported, strategy.GetRetryDelay(new RetryAttempt(1, null, future, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WhenTheHeaderIsZeroSeconds_TheDelayIsZero()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "0");

        Assert.AreEqual(TimeSpan.Zero, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDateIsTheCurrentTime_TheDelayIsZero()
    {
        // The header holds whole seconds, so the fake time is set to a whole second.
        this.timeProvider.SetUtcNow(new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero));
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(this.timeProvider.GetUtcNow()));

        Assert.AreEqual(TimeSpan.Zero, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderCannotBeParsed_TheDelayIsTheInnerDelay()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        Assert.IsTrue(response.Headers.TryAddWithoutValidation("Retry-After", "soon"));
        Assert.IsNull(response.Headers.RetryAfter);

        Assert.AreEqual(InnerDelay, this.Delay(response));
    }

    [TestMethod]
    public void WhenTheHeaderDeltaIsLongerThanTheInnerMaximumDelay_TheHeaderWins()
    {
        // The server's Retry-After is not limited by the inner strategy's maximum delay, only by the longest delay
        // that a timer supports.
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Exponential(3, InnerDelay, maxDelay: TimeSpan.FromSeconds(2)), this.timeProvider);
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)));

        Assert.AreEqual(TimeSpan.FromMinutes(10), strategy.GetRetryDelay(new RetryAttempt(1, null, response, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WhenTheAttemptHasBothAHeaderAndARejectionByTheCircuit_TheHeaderWins()
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);
        using HttpResponseMessage response = Response(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));
        BrokenCircuitException rejection = new("The circuit is open.", TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromSeconds(5), strategy.GetRetryDelay(new RetryAttempt(1, rejection, response, TimeSpan.Zero)));
    }

    [TestMethod]
    [DataRow(-5_000d, 0d)]
    [DataRow(0d, 0d)]
    [DataRow(double.MaxValue, 4294967294d)]
    public void WhenTheRejectionHasARetryAfterOutsideTheTimerRange_TheDelayIsLimited(double retryAfterMilliseconds, double expectedMilliseconds)
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);
        TimeSpan retryAfter = retryAfterMilliseconds == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(retryAfterMilliseconds);
        BrokenCircuitException rejection = new("The circuit is open.", retryAfter);

        Assert.AreEqual(TimeSpan.FromMilliseconds(expectedMilliseconds), strategy.GetRetryDelay(new RetryAttempt(1, rejection, null, TimeSpan.Zero)));
    }

    [TestMethod]
    [DataRow("HeaderDelta")]
    [DataRow("HeaderDate")]
    [DataRow("NoHeader")]
    [DataRow("BreakerRejection")]
    [DataRow("PlainException")]
    public void TheInnerStrategyReceivesTheAttemptUnchanged(string kind)
    {
        List<RetryAttempt> seen = [];
        IRetryStrategy strategy = RetryStrategies.RetryAfter(
            RetryStrategies.FromDelegate(a =>
            {
                seen.Add(a);
                return InnerDelay;
            }),
            this.timeProvider);
        using HttpResponseMessage? response = kind switch
        {
            "HeaderDelta" => Response(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))),
            "HeaderDate" => Response(new RetryConditionHeaderValue(this.timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30))),
            "NoHeader" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => null,
        };
        Exception? exception = kind switch
        {
            "BreakerRejection" => new BrokenCircuitException("The circuit is open.", TimeSpan.FromSeconds(3)),
            "PlainException" => new HttpRequestException("Failed"),
            _ => null,
        };

        // Every field differs from its default, so that a decorator that changes any of them is detected.
        RetryAttempt attempt = new(3, exception, response, TimeSpan.FromSeconds(7));
        strategy.GetRetryDelay(attempt);

        RetryAttempt received = Assert.ContainsSingle(seen);
        Assert.AreEqual(attempt, received);
        Assert.AreSame(exception, received.Exception);
        Assert.AreSame(response, received.Result);
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
    public void WhenAnOpenCircuitRejectedTheAttempt_TheDelayIsTheRetryAfterOfTheRejection()
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);
        BrokenCircuitException rejection = new("The circuit is open.", TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromSeconds(3), strategy.GetRetryDelay(new RetryAttempt(1, rejection, null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WhenTheRejectionHasNoRetryAfter_TheDelayIsTheInnerDelay()
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(3, InnerDelay), this.timeProvider);

        Assert.AreEqual(InnerDelay, strategy.GetRetryDelay(new RetryAttempt(1, new BrokenCircuitException("Half-open"), null, TimeSpan.Zero)));
        Assert.AreEqual(InnerDelay, strategy.GetRetryDelay(new RetryAttempt(1, new IsolatedCircuitException(), null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void WhenTheInnerStrategyRefusesARetry_ItRefusesARetryForARejectionByTheCircuit()
    {
        IRetryStrategy strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(0, InnerDelay), this.timeProvider);

        Assert.IsNull(strategy.GetRetryDelay(new RetryAttempt(1, new BrokenCircuitException("Open", TimeSpan.FromSeconds(3)), null, TimeSpan.Zero)));
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
