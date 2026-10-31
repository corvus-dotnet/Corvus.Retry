// <copyright file="RetryHandlerHttpClientBuilderExtensions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds a <see cref="RetryHandler"/> to the handlers of an <see cref="HttpClient"/> that <c>IHttpClientFactory</c>
/// makes.
/// </summary>
/// <remarks>
/// <para>
/// <c>IHttpClientFactory</c> builds a new chain of handlers from time to time (every two minutes by default), and each
/// chain gets a new <see cref="RetryHandler"/>. Every handler uses the factory that you give, so the clients share
/// its circuit breaker for all of their lifetime. Make the factory once, for example in a static field or as a
/// singleton, and do not make a new factory for each chain.
/// </para>
/// <para>
/// Add the handler after any handler that must run once for each call, and before any handler that must run once for
/// each attempt. <c>IHttpClientFactory</c> runs the handlers in the order that you add them, and the primary handler
/// is last.
/// </para>
/// </remarks>
public static class RetryHandlerHttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="RetryHandler"/> that runs each request through a factory.
    /// </summary>
    /// <param name="builder">The builder of the client.</param>
    /// <param name="factory">The factory that runs each request. Every handler that this call adds uses it.</param>
    /// <returns><paramref name="builder"/>, so that you can add more settings.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="factory"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The handler retries a request only when <see cref="RetryHandler.IsSafeToRetry(HttpRequestMessage)"/> returns
    /// <see langword="true"/> for it. Any other request gets one attempt, with the same timeouts, circuit breaker and
    /// fallback.
    /// </remarks>
    public static IHttpClientBuilder AddRetryHandler(this IHttpClientBuilder builder, RetryTaskFactory<HttpResponseMessage> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.AddHttpMessageHandler(() => new RetryHandler(factory));
    }

    /// <summary>
    /// Adds a <see cref="RetryHandler"/> that runs each request through a factory, and uses a predicate to decide which
    /// requests it can retry.
    /// </summary>
    /// <param name="builder">The builder of the client.</param>
    /// <param name="factory">The factory that runs each request. Every handler that this call adds uses it.</param>
    /// <param name="canRetryRequest">
    /// The predicate that returns <see langword="true"/> when the handler can retry a request. For example, use
    /// <see cref="RetryHandler.CanReplayContent(HttpRequestMessage)"/> to retry a POST whose content can be sent again.
    /// See <see cref="RetryHandler.CanRetryRequest"/>.
    /// </param>
    /// <returns><paramref name="builder"/>, so that you can add more settings.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/>, <paramref name="factory"/> or <paramref name="canRetryRequest"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IHttpClientBuilder AddRetryHandler(
        this IHttpClientBuilder builder,
        RetryTaskFactory<HttpResponseMessage> factory,
        Func<HttpRequestMessage, bool> canRetryRequest)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(canRetryRequest);

        return builder.AddHttpMessageHandler(() => new RetryHandler(factory) { CanRetryRequest = canRetryRequest });
    }
}
