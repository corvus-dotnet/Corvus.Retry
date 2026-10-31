// <copyright file="RetryHandler.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Strategies;

/// <summary>
/// A <see cref="DelegatingHandler"/> that sends each request through a <see cref="RetryTaskFactory{T}"/>, so that an
/// <see cref="HttpClient"/> retries its requests without a wrapper around each call.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="SendAsync(HttpRequestMessage, CancellationToken)"/> calls
/// <see cref="RetryTaskFactory{T}.Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>, and each attempt sends
/// the request to the inner handler. Every setting of the factory applies: the strategy and the policy,
/// <see cref="RetryTaskFactory{T}.CanRetryResult"/>, the timeouts, <see cref="RetryTaskFactory{T}.OnRetry"/>, the
/// circuit breaker and the fallback. The factory disposes each response that
/// <see cref="RetryTaskFactory{T}.CanRetryResult"/> rejects before the next attempt. Make one factory, and give it to
/// every handler that calls the same service, so that they share its circuit breaker.
/// </para>
/// <para>
/// Each attempt sends the same <see cref="HttpRequestMessage"/>. <see cref="HttpClient"/> refuses to send a request
/// message twice, but it makes that check before the request reaches the handlers, so the handler can send the message
/// again. The content of the request is sent again on each attempt, so the handler retries a request only when
/// <see cref="CanRetryRequest"/> returns <see langword="true"/>. By default, that is
/// <see cref="IsSafeToRetry(HttpRequestMessage)"/>: a GET, HEAD, PUT, DELETE, OPTIONS or TRACE request whose content
/// can be sent again. Any other request gets one attempt, with the same timeouts, circuit breaker and fallback.
/// </para>
/// <para>
/// The handler is between <see cref="HttpClient"/> and the inner handler, and this has these effects:
/// <list type="bullet">
/// <item>
/// The handler returns a response when its headers arrive. When the caller asks <see cref="HttpClient"/> to read the
/// whole body (the default of <see cref="HttpClient.GetAsync(Uri)"/> and the other methods), <see cref="HttpClient"/>
/// reads it after the handler returns. Thus a failure while the body downloads is not retried, and
/// <see cref="RetryTaskFactory{T}.AttemptTimeout"/> does not limit the time to read the body. When this matters, use
/// the factory around the whole call instead, for example
/// <c>factory.Run(ct =&gt; client.GetStringAsync(uri, ct), cancellationToken)</c> with a
/// <see cref="RetryTaskFactory{T}"/> of <see cref="string"/>.
/// </item>
/// <item>
/// <see cref="HttpClient.Timeout"/> (100 seconds by default) is outside the handler. It limits all the attempts and
/// all the delays together. When it expires, the call throws a <see cref="TaskCanceledException"/> whose inner
/// exception is a <see cref="TimeoutException"/>, and the fallback does not apply, because the handler sees it as a
/// cancellation by the caller. Make <see cref="RetryTaskFactory{T}.TotalTimeout"/> shorter than
/// <see cref="HttpClient.Timeout"/>, so that the factory stops first.
/// </item>
/// <item>
/// An attempt timeout, or the total timeout of the factory, gets to the caller as a
/// <see cref="RetryTimeoutException"/>, not a <see cref="TaskCanceledException"/>.
/// </item>
/// </list>
/// </para>
/// <para>
/// The handler does not hedge, because attempts at the same time cannot share one <see cref="HttpRequestMessage"/>.
/// It does not support the synchronous <see cref="HttpClient.Send(HttpRequestMessage)"/>: see
/// <see cref="Send(HttpRequestMessage, CancellationToken)"/>.
/// </para>
/// <para>
/// A handler is safe for many threads. Many requests can use it at the same time. Each <see cref="HttpClient"/> or
/// handler chain needs its own handler instance, because <see cref="DelegatingHandler.InnerHandler"/> belongs to one
/// chain, but the instances can share one factory.
/// </para>
/// </remarks>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryTaskFactory<HttpResponseMessage> singleAttemptFactory;
    private readonly Func<HttpRequestMessage, bool> canRetryRequest = IsSafeToRetry;

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryHandler"/> class, with no inner handler.
    /// </summary>
    /// <param name="factory">The factory that runs each request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Set <see cref="DelegatingHandler.InnerHandler"/> before the first request, or add the handler to an
    /// <c>IHttpClientFactory</c> pipeline, which sets it.
    /// </remarks>
    public RetryHandler(RetryTaskFactory<HttpResponseMessage> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        this.Factory = factory;
        this.singleAttemptFactory = CreateSingleAttemptFactory(factory);
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryHandler"/> class, with an inner handler.
    /// </summary>
    /// <param name="factory">The factory that runs each request.</param>
    /// <param name="innerHandler">The handler that sends each attempt, for example a <see cref="SocketsHttpHandler"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="factory"/> or <paramref name="innerHandler"/> is <see langword="null"/>.
    /// </exception>
    public RetryHandler(RetryTaskFactory<HttpResponseMessage> factory, HttpMessageHandler innerHandler)
        : base(CheckArguments(factory, innerHandler))
    {
        this.Factory = factory;
        this.singleAttemptFactory = CreateSingleAttemptFactory(factory);
    }

    /// <summary>
    /// Gets the factory that runs each request that <see cref="CanRetryRequest"/> accepts.
    /// </summary>
    public RetryTaskFactory<HttpResponseMessage> Factory { get; }

    /// <summary>
    /// Gets a predicate that returns <see langword="true"/> when the handler can retry a request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="IsSafeToRetry(HttpRequestMessage)"/>. To retry a POST or a PATCH whose content can be
    /// sent again, for example because the service ignores a repeated request with the same idempotency key, use
    /// <see cref="CanReplayContent(HttpRequestMessage)"/>.
    /// </para>
    /// <para>
    /// The handler calls the predicate once for each request, before the first attempt. A request that the predicate
    /// refuses gets one attempt. That attempt uses a copy of <see cref="Factory"/> whose strategy is
    /// <see cref="DoNotRetry"/>, so the timeouts, the circuit breaker, <see cref="RetryTaskFactory{T}.CanRetryResult"/>
    /// and the fallback still apply. A response that <see cref="RetryTaskFactory{T}.CanRetryResult"/> rejects is
    /// returned to the caller, or gets the fallback value, and the circuit breaker counts it as a failure.
    /// </para>
    /// <para>
    /// An exception from the predicate gets to the caller, and the request is not sent.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<HttpRequestMessage, bool> CanRetryRequest
    {
        get => this.canRetryRequest;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(this.CanRetryRequest));
            this.canRetryRequest = value;
        }
    }

    /// <summary>
    /// Returns a value that indicates whether a request is safe to send more than once: its method is safe or
    /// idempotent, and its content can be sent again.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>
    /// <see langword="true"/> when the method is GET, HEAD, PUT, DELETE, OPTIONS or TRACE, and
    /// <see cref="CanReplayContent(HttpRequestMessage)"/> returns <see langword="true"/>; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// RFC 9110 defines these methods as idempotent: when a client sends the same request more than once, the effect on
    /// the server is the same as for one request. POST and PATCH are not idempotent.
    /// </remarks>
    public static bool IsSafeToRetry(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpMethod method = request.Method;
        bool idempotent =
            method == HttpMethod.Get ||
            method == HttpMethod.Head ||
            method == HttpMethod.Put ||
            method == HttpMethod.Delete ||
            method == HttpMethod.Options ||
            method == HttpMethod.Trace;

        return idempotent && CanReplayContent(request);
    }

    /// <summary>
    /// Returns a value that indicates whether the content of a request can be sent again, whatever its method.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>
    /// <see langword="true"/> when the request has no content, or its content is a <see cref="ByteArrayContent"/>
    /// (which includes <see cref="StringContent"/> and <see cref="FormUrlEncodedContent"/>), a
    /// <see cref="ReadOnlyMemoryContent"/> or a <see cref="JsonContent"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// These content types write the same bytes each time they are sent. Other content types, for example
    /// <see cref="StreamContent"/> and <see cref="MultipartContent"/>, may read a stream that cannot be read again, so
    /// this method returns <see langword="false"/> for them.
    /// </remarks>
    public static bool CanReplayContent(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Content is null or ByteArrayContent or ReadOnlyMemoryContent or JsonContent;
    }

    /// <summary>
    /// Sends a request to the inner handler, and retries it when <see cref="CanRetryRequest"/> permits.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">
    /// The token of the call. <see cref="HttpClient"/> cancels it when its caller cancels or when
    /// <see cref="HttpClient.Timeout"/> expires.
    /// </param>
    /// <returns>
    /// The response of the first attempt that succeeds, or the last response if no retries remain. When the fallback
    /// applies, the value of the fallback.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The handler has no inner handler.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout of the factory expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Without this check, every attempt would fail with the same exception, and a policy could retry it.
        if (this.InnerHandler is null)
        {
            throw new InvalidOperationException("The RetryHandler has no inner handler. Set InnerHandler before the first request.");
        }

        RetryTaskFactory<HttpResponseMessage> factory = this.canRetryRequest(request) ? this.Factory : this.singleAttemptFactory;
        return factory.Run(ct => base.SendAsync(request, ct), cancellationToken);
    }

    /// <summary>
    /// Throws a <see cref="NotSupportedException"/>, because the handler supports only asynchronous requests.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// <see cref="DelegatingHandler"/> would send a synchronous request straight to the inner handler, with no retries,
    /// so the handler refuses it. Use <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/> or the
    /// other asynchronous methods of <see cref="HttpClient"/>.
    /// </remarks>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("RetryHandler does not support synchronous requests, because it cannot retry them. Use HttpClient.SendAsync or another asynchronous method.");
    }

    // Runs before the base constructor, so that a null inner handler is reported with the name of this parameter.
    private static HttpMessageHandler CheckArguments(RetryTaskFactory<HttpResponseMessage> factory, HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(innerHandler);

        return innerHandler;
    }

    private static RetryTaskFactory<HttpResponseMessage> CreateSingleAttemptFactory(RetryTaskFactory<HttpResponseMessage> factory)
    {
        return new RetryTaskFactory<HttpResponseMessage>(factory) { Strategy = new DoNotRetry() };
    }
}
