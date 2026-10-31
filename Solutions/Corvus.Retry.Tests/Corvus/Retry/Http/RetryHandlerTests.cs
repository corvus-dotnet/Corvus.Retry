// <copyright file="RetryHandlerTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="RetryHandler"/> retries requests that an <see cref="HttpClient"/> sends, with every setting of its
/// factory. Each test sends through a real <see cref="HttpClient"/>, so that the behaviour of the client around the
/// handler (its check that a request is sent once, its timeout, its exceptions and its buffering of the body) is part
/// of the test.
/// </summary>
[TestClass]
public class RetryHandlerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly Uri Address = new("https://catalogue.example/products/42");

    [TestMethod]
    public async Task A503ResponseIsRetriedUntilA200Arrives()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.OK, "stock: 7"));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);

        using HttpResponseMessage response = await client.GetAsync(Address).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("stock: 7", await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(3, inner.Calls);
    }

    [TestMethod]
    public async Task EachRejectedResponseIsDisposedAndTheReturnedResponseIsNot()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.TooManyRequests),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);

        using HttpResponseMessage response = await client.GetAsync(Address).ConfigureAwait(false);

        bool[] disposed = [.. inner.Responses.Select(r => ((TrackingContent)r.Content).Disposed)];
        Assert.AreSequenceEqual(new[] { true, true, false }, disposed);
        Assert.AreSame(inner.Responses[2], response);
    }

    [TestMethod]
    public async Task WhenNoRetriesRemain_TheLastRejectedResponseIsReturned()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);

        using HttpResponseMessage response = await client.GetAsync(Address).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(3, inner.Calls);
        Assert.IsFalse(((TrackingContent)response.Content).Disposed);
    }

    [TestMethod]
    public async Task AnHttpRequestExceptionIsRetried()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Throw(new HttpRequestException("The connection was refused.")),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);

        using HttpResponseMessage response = await client.GetAsync(Address).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task WhenNoRetriesRemain_TheLastHttpRequestExceptionGetsToTheCaller()
    {
        var last = new HttpRequestException("The connection was refused again.");
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Throw(new HttpRequestException("The connection was refused.")),
            ScriptedHttpHandler.Throw(last));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider(), tries: 2), inner);

        HttpRequestException thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync(Address)).ConfigureAwait(false);

        Assert.AreSame(last, thrown);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task A429WithRetryAfterOf5Seconds_WaitsFor5Seconds()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan fiveSeconds = TimeSpan.FromSeconds(5);
        var inner = new ScriptedHttpHandler(
            (_, _) =>
            {
                var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(fiveSeconds);
                return Task.FromResult(tooMany);
            },
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        var factory = new RetryTaskFactory<HttpResponseMessage>(
            RetryStrategies.RetryAfter(RetryStrategies.Constant(3, TimeSpan.FromSeconds(1)), fake),
            RetryPolicies.TransientHttp)
        {
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = fake,
        };
        using HttpClient client = CreateClient(factory, inner);

        Task<HttpResponseMessage> call = client.GetAsync(Address);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { fiveSeconds }, fake.Delays);
        fake.Advance(fiveSeconds - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, inner.Calls);
        fake.Advance(TimeSpan.FromTicks(1));

        using HttpResponseMessage response = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task EachAttemptSendsTheSameRequestMessage()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address);

        using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);

        Assert.HasCount(3, inner.Requests);
        Assert.IsTrue(inner.Requests.All(r => ReferenceEquals(r, request)));
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    public async Task HttpClientStillRefusesToSendARequestMessageTwice()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address);
        using HttpResponseMessage first = await client.SendAsync(request).ConfigureAwait(false);

        // The check is in HttpClient, before the handlers, so the handler can send the message again but a caller cannot.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.SendAsync(request)).ConfigureAwait(false);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task APutWithStringContentSendsTheSameBodyOnEachAttempt()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);
        using var content = new StringContent("""{ "stock": 7 }""");

        using HttpResponseMessage response = await client.PutAsync(Address, content).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreSequenceEqual(new[] { """{ "stock": 7 }""", """{ "stock": 7 }""" }, inner.Bodies);
        Assert.AreSame(inner.Requests[0], inner.Requests[1]);
    }

    [TestMethod]
    public async Task APutWithStreamContentGetsOneAttempt()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);
        using var content = new StreamContent(new TestContents.NonSeekableStream("payload"u8.ToArray()));

        using HttpResponseMessage response = await client.PutAsync(Address, content).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, inner.Calls);
        Assert.AreSequenceEqual(new[] { "payload" }, inner.Bodies);
    }

    [TestMethod]
    public async Task WhenAStreamContentIsRetriedAnyway_TheSecondAttemptCannotReadTheStream()
    {
        // This is why CanReplayContent refuses a StreamContent: its stream can be read only once.
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var factory = new RetryTaskFactory<HttpResponseMessage>
        {
            Strategy = new Count(2),
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = new RecordingFakeTimeProvider(),
        };
        using HttpClient client = CreateClient(new RetryHandler(factory, inner) { CanRetryRequest = _ => true });
        using var content = new StreamContent(new TestContents.NonSeekableStream("payload"u8.ToArray()));

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.PutAsync(Address, content)).ConfigureAwait(false);

        Assert.Contains("already consumed", thrown.Message);

        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task APostGetsOneAttemptByDefault()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var retries = new List<int>();
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(new RecordingFakeTimeProvider()))
        {
            OnRetry = args =>
            {
                retries.Add(args.AttemptNumber);
                return ValueTask.CompletedTask;
            },
        };
        using HttpClient client = CreateClient(factory, inner);
        using var content = new StringContent("order");

        using HttpResponseMessage response = await client.PostAsync(Address, content).ConfigureAwait(false);

        // The single attempt still applies CanRetryResult, so it returns the rejected response without disposing it.
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsFalse(((TrackingContent)response.Content).Disposed);
        Assert.AreEqual(1, inner.Calls);
        Assert.IsEmpty(retries);
    }

    [TestMethod]
    public async Task APostIsRetriedWhenCanRetryRequestIsCanReplayContent()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.Created));
        using HttpClient client = CreateClient(
            new RetryHandler(TransientFactory(new RecordingFakeTimeProvider()), inner) { CanRetryRequest = RetryHandler.CanReplayContent });
        using var content = new StringContent("order");

        using HttpResponseMessage response = await client.PostAsync(Address, content).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreSequenceEqual(new[] { "order", "order" }, inner.Bodies);
    }

    [TestMethod]
    public async Task ARequestThatCannotBeRetriedStillGetsTheAttemptTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Hang());
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(fake)) { AttemptTimeout = TimeSpan.FromSeconds(1) };
        using HttpClient client = CreateClient(factory, inner);
        using var content = new StringContent("order");

        Task<HttpResponseMessage> call = client.PostAsync(Address, content);
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task AnAttemptTimeoutIsRetried_AndTheLastGetsToTheCallerAsRetryTimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(1);
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Hang());
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(fake, tries: 2)) { AttemptTimeout = attemptTimeout };
        using HttpClient client = CreateClient(factory, inner);

        Task<HttpResponseMessage> call = client.GetAsync(Address);
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        // HttpClient turns an OperationCanceledException into a TaskCanceledException, but a RetryTimeoutException is a
        // TimeoutException, so it gets to the caller unchanged.
        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task TheTotalTimeoutGetsToTheCallerAsRetryTimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(30);
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var factory = new RetryTaskFactory<HttpResponseMessage>(RetryStrategies.Constant(10, TimeSpan.FromSeconds(20)), RetryPolicies.TransientHttp)
        {
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TotalTimeout = totalTimeout,
            TimeProvider = fake,
        };
        using HttpClient client = CreateClient(factory, inner);

        Task<HttpResponseMessage> call = client.GetAsync(Address);
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromSeconds(20));
        await fake.WaitForTimersAsync(3).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromSeconds(10));

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task HttpClientTimeoutEndsAHungAttempt_AsATaskCanceledExceptionWithATimeoutException()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Hang());
        var fellBack = false;
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(new RecordingFakeTimeProvider()))
        {
            Fallback = new(_ =>
            {
                fellBack = true;
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }),
        };
        using HttpClient client = CreateClient(factory, inner);
        client.Timeout = TimeSpan.FromMilliseconds(100);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.GetAsync(Address).WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The handler sees the timeout of HttpClient as a cancellation by its caller: it does not retry, and the
        // fallback does not apply.
        Assert.IsInstanceOfType<TimeoutException>(thrown.InnerException);
        Assert.AreEqual(1, inner.Calls);
        Assert.IsFalse(fellBack);
    }

    [TestMethod]
    public async Task HttpClientTimeoutLimitsTheDelaysToo()
    {
        RecordingFakeTimeProvider fake = new();
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var factory = new RetryTaskFactory<HttpResponseMessage>(RetryStrategies.Constant(3, TimeSpan.FromMinutes(5)), RetryPolicies.TransientHttp)
        {
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = fake,
        };
        using HttpClient client = CreateClient(factory, inner);
        client.Timeout = TimeSpan.FromMilliseconds(100);

        // The fake time never moves, so the delay of 5 minutes ends only when HttpClient.Timeout cancels it.
        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.GetAsync(Address).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.IsInstanceOfType<TimeoutException>(thrown.InnerException);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromMinutes(5) }, fake.Delays);
        Assert.AreEqual(1, inner.Calls);
        Assert.IsTrue(((TrackingContent)inner.Responses[0].Content).Disposed);
    }

    [TestMethod]
    public async Task ACancellationByTheCallerIsNotRetried_AndCarriesTheCallersToken()
    {
        RecordingFakeTimeProvider fake = new();
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Hang());
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(fake)) { AttemptTimeout = TimeSpan.FromSeconds(10) };
        using HttpClient client = CreateClient(factory, inner);
        using var cts = new CancellationTokenSource();

        Task<HttpResponseMessage> call = client.GetAsync(Address, cts.Token);
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsNotInstanceOfType<TimeoutException>(thrown.InnerException);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public void TheSynchronousSendThrowsNotSupportedException()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(TransientFactory(new RecordingFakeTimeProvider()), inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address);

        NotSupportedException thrown = Assert.ThrowsExactly<NotSupportedException>(() => client.Send(request));

        Assert.Contains("SendAsync", thrown.Message);
        Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    public async Task AnOpenCircuitRejectsTheNextRequestWithoutSendingIt()
    {
        RecordingFakeTimeProvider fake = new();
        var breaker = new CircuitBreaker(failureRate: 0.5, minimumCalls: 2, openDuration: TimeSpan.FromMinutes(1), timeProvider: fake);
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(fake, tries: 2)) { CircuitBreaker = breaker };
        using HttpClient client = CreateClient(factory, inner);

        using HttpResponseMessage first = await client.GetAsync(Address).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Open, breaker.State);

        // TransientHttp does not retry the rejection, and HttpClient passes the exception to the caller unchanged.
        CircuitRejectedException thrown = await Assert.ThrowsAsync<CircuitRejectedException>(() => client.GetAsync(Address)).ConfigureAwait(false);
        Assert.AreEqual(TimeSpan.FromMinutes(1), thrown.RetryAfter);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task TheFallbackGivesTheResponseWhenTheRetriesFail()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = CreateClient(CachedFallbackFactory(), inner);

        string body = await client.GetStringAsync(Address).ConfigureAwait(false);

        Assert.AreEqual("cached", body);
        Assert.AreEqual(2, inner.Calls);
        Assert.IsTrue(inner.Responses.All(r => ((TrackingContent)r.Content).Disposed));
    }

    [TestMethod]
    public async Task TheFallbackAppliesToARequestThatCannotBeRetried()
    {
        var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = CreateClient(CachedFallbackFactory(), inner);
        using var content = new StringContent("order");

        using HttpResponseMessage response = await client.PostAsync(Address, content).ConfigureAwait(false);

        Assert.AreEqual("cached", await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task AFailureWhileHttpClientBuffersTheBodyIsNotRetried()
    {
        var body = new TestContents.FailingContent();
        var inner = new ScriptedHttpHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));

        // The default factory retries any exception, so an exception inside the handler would be retried.
        using HttpClient client = CreateClient(new RetryTaskFactory<HttpResponseMessage> { TimeProvider = new RecordingFakeTimeProvider() }, inner);

        HttpRequestException thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync(Address)).ConfigureAwait(false);

        Assert.IsInstanceOfType<System.IO.IOException>(thrown.InnerException);
        Assert.AreEqual(1, body.Reads);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task WithResponseHeadersRead_TheCallSucceedsAndTheBodyFailsLater()
    {
        var body = new TestContents.FailingContent();
        var inner = new ScriptedHttpHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }),
            ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using HttpClient client = CreateClient(new RetryTaskFactory<HttpResponseMessage> { TimeProvider = new RecordingFakeTimeProvider() }, inner);

        using HttpResponseMessage response = await client.GetAsync(Address, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        Assert.AreEqual(0, body.Reads);

        HttpRequestException thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync()).ConfigureAwait(false);
        Assert.IsInstanceOfType<System.IO.IOException>(thrown.InnerException);
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task TheAttemptTimeoutDoesNotLimitTheTimeToReadABufferedBody()
    {
        RecordingFakeTimeProvider fake = new();
        var body = new TestContents.PendingContent();
        var inner = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(fake)) { AttemptTimeout = TimeSpan.FromSeconds(1) };
        using HttpClient client = CreateClient(factory, inner);

        Task<HttpResponseMessage> call = client.GetAsync(Address);
        await body.Started.WaitAsync(WaitLimit).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromHours(1));
        Assert.IsFalse(call.IsCompleted);
        body.Complete();

        using HttpResponseMessage response = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual("the body", await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(1, inner.Calls);
    }

    [TestMethod]
    public async Task ThroughSocketsHttpHandler_APutWithStringContentIsSentAgainWithItsBody()
    {
        using var server = new LoopbackHttpServer(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using HttpClient client = CreateClient(new RetryHandler(TransientFactory(new RecordingFakeTimeProvider()), new SocketsHttpHandler()));
        using var content = new StringContent("""{ "stock": 7 }""");

        using HttpResponseMessage response = await client.PutAsync(new Uri(server.BaseAddress, "products/42"), content).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyList<(string Method, string Body)> received = await server.WaitForRequestsAsync(2, WaitLimit).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { ("PUT", """{ "stock": 7 }"""), ("PUT", """{ "stock": 7 }""") }, received.ToArray());
    }

    [TestMethod]
    public async Task WithoutAnInnerHandler_SendAsyncThrowsInvalidOperationExceptionAndMakesNoAttempt()
    {
        int attempts = 0;
        RetryTaskFactory<HttpResponseMessage> factory = new(TransientFactory(new RecordingFakeTimeProvider()))
        {
            OnRetry = _ =>
            {
                attempts++;
                return ValueTask.CompletedTask;
            },
        };
        using var invoker = new HttpMessageInvoker(new RetryHandler(factory));
        using var request = new HttpRequestMessage(HttpMethod.Get, Address);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => invoker.SendAsync(request, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public void TheConstructorsRefuseNull()
    {
        var factory = new RetryTaskFactory<HttpResponseMessage>();
        using var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK));

        Assert.AreEqual("factory", Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(null!)).ParamName);
        Assert.AreEqual("factory", Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(null!, inner)).ParamName);
        Assert.AreEqual("innerHandler", Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(factory, null!)).ParamName);
        Assert.AreEqual("CanRetryRequest", Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(factory) { CanRetryRequest = null! }).ParamName);
    }

    [TestMethod]
    public void TheHandlerKeepsItsFactoryAndInnerHandler_AndDefaultsToIsSafeToRetry()
    {
        var factory = new RetryTaskFactory<HttpResponseMessage>();
        using var inner = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK));
        using var handler = new RetryHandler(factory, inner);

        Assert.AreSame(factory, handler.Factory);
        Assert.AreSame(inner, handler.InnerHandler);
        Assert.AreEqual<Func<HttpRequestMessage, bool>>(RetryHandler.IsSafeToRetry, handler.CanRetryRequest);
    }

    [TestMethod]
    [DataRow("GET", true)]
    [DataRow("HEAD", true)]
    [DataRow("PUT", true)]
    [DataRow("DELETE", true)]
    [DataRow("OPTIONS", true)]
    [DataRow("TRACE", true)]
    [DataRow("POST", false)]
    [DataRow("PATCH", false)]
    [DataRow("CONNECT", false)]
    [DataRow("PURGE", false)]
    public void IsSafeToRetryAcceptsOnlyIdempotentMethods(string method, bool expected)
    {
        using var withoutContent = new HttpRequestMessage(new HttpMethod(method), Address);
        using var withContent = new HttpRequestMessage(new HttpMethod(method), Address) { Content = new StringContent("x") };

        Assert.AreEqual(expected, RetryHandler.IsSafeToRetry(withoutContent));
        Assert.AreEqual(expected, RetryHandler.IsSafeToRetry(withContent));
    }

    [TestMethod]
    [DataRow("none", true)]
    [DataRow("ByteArrayContent", true)]
    [DataRow("StringContent", true)]
    [DataRow("FormUrlEncodedContent", true)]
    [DataRow("ReadOnlyMemoryContent", true)]
    [DataRow("JsonContent", true)]
    [DataRow("StreamContent", false)]
    [DataRow("MultipartContent", false)]
    [DataRow("MultipartFormDataContent", false)]
    public void CanReplayContentAcceptsOnlyContentThatWritesTheSameBytesEachTime(string contentType, bool expected)
    {
        using HttpContent? content = contentType switch
        {
            "none" => null,
            "ByteArrayContent" => new ByteArrayContent([1, 2, 3]),
            "StringContent" => new StringContent("x"),
            "FormUrlEncodedContent" => new FormUrlEncodedContent([new("a", "b")]),
            "ReadOnlyMemoryContent" => new ReadOnlyMemoryContent(new byte[] { 1, 2, 3 }),
            "JsonContent" => System.Net.Http.Json.JsonContent.Create(42),
            "StreamContent" => new StreamContent(new System.IO.MemoryStream([1, 2, 3])),
            "MultipartContent" => new MultipartContent { new StringContent("x") },
            "MultipartFormDataContent" => new MultipartFormDataContent { new StringContent("x") },
            _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
        };
        using var put = new HttpRequestMessage(HttpMethod.Put, Address) { Content = content };
        using var post = new HttpRequestMessage(HttpMethod.Post, Address) { Content = content };

        Assert.AreEqual(expected, RetryHandler.CanReplayContent(put));
        Assert.AreEqual(expected, RetryHandler.CanReplayContent(post));
        Assert.AreEqual(expected, RetryHandler.IsSafeToRetry(put));
        Assert.IsFalse(RetryHandler.IsSafeToRetry(post));
    }

    [TestMethod]
    public void TheStaticPredicatesRefuseNull()
    {
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() => RetryHandler.IsSafeToRetry(null!)).ParamName);
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() => RetryHandler.CanReplayContent(null!)).ParamName);
    }

    // Up to 3 tries without delay. It retries transient exceptions, attempt timeouts and transient responses.
    private static RetryTaskFactory<HttpResponseMessage> TransientFactory(TimeProvider timeProvider, int tries = 3)
    {
        return new RetryTaskFactory<HttpResponseMessage>(new Count(tries), RetryPolicies.TransientHttp)
        {
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            TimeProvider = timeProvider,
        };
    }

    private static RetryTaskFactory<HttpResponseMessage> CachedFallbackFactory()
    {
        return new RetryTaskFactory<HttpResponseMessage>(TransientFactory(new RecordingFakeTimeProvider(), tries: 2))
        {
            Fallback = new(_ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cached") }))
            {
                AppliesToResult = RetryPolicies.IsTransientHttpResponse,
            },
        };
    }

    private static HttpClient CreateClient(RetryTaskFactory<HttpResponseMessage> factory, HttpMessageHandler inner)
    {
        return CreateClient(new RetryHandler(factory, inner));
    }

    private static HttpClient CreateClient(RetryHandler handler)
    {
        return new HttpClient(handler);
    }
}
