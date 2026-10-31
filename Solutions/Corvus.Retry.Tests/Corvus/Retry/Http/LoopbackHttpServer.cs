// <copyright file="LoopbackHttpServer.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A small HTTP server on the loopback interface, so that a test can send through a real
/// <see cref="System.Net.Http.SocketsHttpHandler"/>. It answers each request with the next status code of a script
/// (the last one repeats), and records the method and the body of each request.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly Lock sync = new();
    private readonly List<(string Method, string Body)> requests = [];
    private readonly HttpStatusCode[] statuses;
    private readonly HttpListener listener;
    private readonly Task loop;

    public LoopbackHttpServer(params HttpStatusCode[] statuses)
    {
        this.statuses = statuses;
        (this.listener, this.BaseAddress) = Start();
        this.loop = this.ServeAsync();
    }

    public Uri BaseAddress { get; }

    public async Task<IReadOnlyList<(string Method, string Body)>> WaitForRequestsAsync(int count, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            lock (this.sync)
            {
                if (this.requests.Count >= count)
                {
                    return this.requests.ToArray();
                }
            }

            if (stopwatch.Elapsed > timeout)
            {
                Assert.Fail($"Expected {count} requests within {timeout}.");
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        this.listener.Close();
        try
        {
            this.loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener throws from GetContextAsync when it closes.
        }
    }

    private static (HttpListener Listener, Uri BaseAddress) Start()
    {
        // A port that was free a moment ago can be taken before the listener starts, so try a few.
        for (int i = 0; ; i++)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var baseAddress = new Uri($"http://localhost:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(baseAddress.ToString());
            try
            {
                listener.Start();
                return (listener, baseAddress);
            }
            catch (HttpListenerException) when (i < 5)
            {
                listener.Close();
            }
        }
    }

    private async Task ServeAsync()
    {
        while (this.listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);
            HttpStatusCode status;
            lock (this.sync)
            {
                status = this.statuses[Math.Min(this.requests.Count, this.statuses.Length - 1)];
                this.requests.Add((context.Request.HttpMethod, body));
            }

            context.Response.StatusCode = (int)status;
            context.Response.Close();
        }
    }
}
