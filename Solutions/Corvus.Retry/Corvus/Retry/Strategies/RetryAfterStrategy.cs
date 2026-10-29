// <copyright file="RetryAfterStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Net.Http;
using System.Net.Http.Headers;

/// <summary>
/// A <see cref="IRetryStrategy"/> that uses the <c>Retry-After</c> header of an <see cref="HttpResponseMessage"/>
/// result, when there is one, and otherwise the delay of an inner strategy.
/// </summary>
/// <remarks>Created by <see cref="RetryStrategies.RetryAfter(IRetryStrategy, TimeProvider?)"/>.</remarks>
internal sealed class RetryAfterStrategy : IRetryStrategy
{
    private readonly IRetryStrategy inner;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryAfterStrategy"/> class.
    /// </summary>
    /// <param name="inner">The strategy that decides whether to retry, and the delay when there is no header.</param>
    /// <param name="timeProvider">The time provider that gives the current time for a header with a date.</param>
    internal RetryAfterStrategy(IRetryStrategy inner, TimeProvider timeProvider)
    {
        this.inner = inner;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (this.inner.GetRetryDelay(attempt) is not TimeSpan delay)
        {
            return null;
        }

        if (attempt.Result is HttpResponseMessage { Headers.RetryAfter: RetryConditionHeaderValue retryAfter })
        {
            if (retryAfter.Delta is TimeSpan delta)
            {
                return RetryDelay.Clamp(delta);
            }

            if (retryAfter.Date is DateTimeOffset date)
            {
                // A date in the past becomes a zero delay.
                return RetryDelay.Clamp(date - this.timeProvider.GetUtcNow());
            }
        }

        return RetryDelay.Clamp(delay);
    }
}
