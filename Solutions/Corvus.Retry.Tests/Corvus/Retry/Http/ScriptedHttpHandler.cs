// <copyright file="ScriptedHttpHandler.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A primary <see cref="HttpMessageHandler"/> that plays a script, one step for each request it receives, and records
/// each request and the bytes of its content. The last step repeats.
/// </summary>
/// <remarks>
/// The handler writes the content of each request to a stream with <see cref="HttpContent.CopyToAsync(Stream, CancellationToken)"/>,
/// as <see cref="SocketsHttpHandler"/> does. It does not call <see cref="HttpContent.ReadAsStringAsync()"/>, because
/// that buffers the content, and a buffered content can be read again whatever its type.
/// </remarks>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Lock sync = new();
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps;
    private readonly List<HttpRequestMessage> requests = [];
    private readonly List<string?> bodies = [];
    private readonly List<HttpResponseMessage> responses = [];

    public ScriptedHttpHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steps.Length);
        this.steps = steps;
    }

    /// <summary>
    /// Gets the number of requests that the handler received.
    /// </summary>
    public int Calls
    {
        get
        {
            lock (this.sync)
            {
                return this.requests.Count;
            }
        }
    }

    /// <summary>
    /// Gets each request that the handler received, in order.
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (this.sync)
            {
                return this.requests.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the content of each request, as UTF-8 text, or <see langword="null"/> for a request with no content.
    /// </summary>
    public IReadOnlyList<string?> Bodies
    {
        get
        {
            lock (this.sync)
            {
                return this.bodies.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets each response that the handler returned, in order.
    /// </summary>
    public IReadOnlyList<HttpResponseMessage> Responses
    {
        get
        {
            lock (this.sync)
            {
                return this.responses.ToArray();
            }
        }
    }

    // A step that returns a response with a status code, and a content that records its disposal.
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode code, string body = "")
    {
        return (_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new TrackingContent(body) });
    }

    // A step that throws an exception.
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(Exception exception)
    {
        return (_, _) => Task.FromException<HttpResponseMessage>(exception);
    }

    // A step that never answers, and ends when its token is cancelled.
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hang()
    {
        return async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("An infinite delay ended without cancellation.");
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is HttpContent content)
        {
            using var stream = new MemoryStream();
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(stream.ToArray());
        }

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step;
        lock (this.sync)
        {
            step = this.steps[Math.Min(this.requests.Count, this.steps.Length - 1)];
            this.requests.Add(request);
            this.bodies.Add(body);
        }

        HttpResponseMessage response = await step(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage = request;
        lock (this.sync)
        {
            this.responses.Add(response);
        }

        return response;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (this.sync)
        {
            this.requests.Add(request);
            this.bodies.Add(null);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
