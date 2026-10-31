// <copyright file="RetryHandlerHttpClientBuilderExtensionsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryHandlerHttpClientBuilderExtensions.AddRetryHandler(IHttpClientBuilder, RetryTaskFactory{HttpResponseMessage})"/>
/// adds a new <see cref="RetryHandler"/> to each chain of handlers that <c>IHttpClientFactory</c> builds, and every
/// handler uses the one factory, with its one circuit breaker.
/// </summary>
[TestClass]
public class RetryHandlerHttpClientBuilderExtensionsTests
{
    private static readonly Uri Address = new("https://catalogue.example/products/42");

    [TestMethod]
    public void EachChainThatTheFactoryBuildsHasANewHandlerWithTheSameFactory()
    {
        RetryTaskFactory<HttpResponseMessage> factory = TransientFactory(new RecordingFakeTimeProvider(), breaker: null);
        var services = new ServiceCollection();
        services.AddHttpClient("catalogue")
            .ConfigurePrimaryHttpMessageHandler(() => new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK)))
            .AddRetryHandler(factory);
        using ServiceProvider provider = services.BuildServiceProvider();

        // IHttpClientFactory builds a chain this way when a client is first made, and again each time the lifetime of
        // the handlers ends (two minutes by default).
        RetryHandler first = BuildChain(provider, "catalogue");
        RetryHandler second = BuildChain(provider, "catalogue");

        Assert.AreNotSame(first, second);
        Assert.AreSame(factory, first.Factory);
        Assert.AreSame(factory, second.Factory);
        Assert.AreEqual<Func<HttpRequestMessage, bool>>(RetryHandler.IsSafeToRetry, first.CanRetryRequest);
    }

    [TestMethod]
    public async Task ClientsWithDifferentChainsShareTheCircuitBreakerOfTheFactory()
    {
        RecordingFakeTimeProvider fake = new();
        var breaker = new CircuitBreaker(failureRate: 0.5, minimumCalls: 2, openDuration: TimeSpan.FromMinutes(1), timeProvider: fake);
        RetryTaskFactory<HttpResponseMessage> factory = TransientFactory(fake, breaker);
        var failing = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var healthy = new ScriptedHttpHandler(ScriptedHttpHandler.Status(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddHttpClient("failing").ConfigurePrimaryHttpMessageHandler(() => failing).AddRetryHandler(factory);
        services.AddHttpClient("healthy").ConfigurePrimaryHttpMessageHandler(() => healthy).AddRetryHandler(factory);
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();

        using (HttpClient client = clients.CreateClient("failing"))
        {
            using HttpResponseMessage response = await client.GetAsync(Address).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);

        using (HttpClient client = clients.CreateClient("healthy"))
        {
            await Assert.ThrowsAsync<CircuitRejectedException>(() => client.GetAsync(Address)).ConfigureAwait(false);
        }

        Assert.AreEqual(2, failing.Calls);
        Assert.AreEqual(0, healthy.Calls);
    }

    [TestMethod]
    public async Task TheOverloadWithAPredicateCanRetryAPost()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.Created));
        var services = new ServiceCollection();
        services.AddHttpClient("orders")
            .ConfigurePrimaryHttpMessageHandler(() => inner)
            .AddRetryHandler(TransientFactory(new RecordingFakeTimeProvider(), breaker: null), RetryHandler.CanReplayContent);
        using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        using var content = new StringContent("order");

        using HttpResponseMessage response = await client.PostAsync(Address, content).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public async Task ATypedClientGetsTheHandler()
    {
        var inner = new ScriptedHttpHandler(
            ScriptedHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpHandler.Status(HttpStatusCode.OK, "stock: 7"));
        var services = new ServiceCollection();
        services.AddHttpClient<CatalogueClient>(c => c.BaseAddress = new Uri("https://catalogue.example/"))
            .ConfigurePrimaryHttpMessageHandler(() => inner)
            .AddRetryHandler(TransientFactory(new RecordingFakeTimeProvider(), breaker: null));
        using ServiceProvider provider = services.BuildServiceProvider();

        string stock = await provider.GetRequiredService<CatalogueClient>().GetStockAsync().ConfigureAwait(false);

        Assert.AreEqual("stock: 7", stock);
        Assert.AreEqual(2, inner.Calls);
    }

    [TestMethod]
    public void AddRetryHandlerRefusesNull()
    {
        IHttpClientBuilder builder = new ServiceCollection().AddHttpClient("x");
        var factory = new RetryTaskFactory<HttpResponseMessage>();

        Assert.AreEqual("builder", Assert.ThrowsExactly<ArgumentNullException>(() => RetryHandlerHttpClientBuilderExtensions.AddRetryHandler(null!, factory)).ParamName);
        Assert.AreEqual("factory", Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddRetryHandler(null!)).ParamName);
        Assert.AreEqual("builder", Assert.ThrowsExactly<ArgumentNullException>(() => RetryHandlerHttpClientBuilderExtensions.AddRetryHandler(null!, factory, RetryHandler.CanReplayContent)).ParamName);
        Assert.AreEqual("factory", Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddRetryHandler(null!, RetryHandler.CanReplayContent)).ParamName);
        Assert.AreEqual("canRetryRequest", Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddRetryHandler(factory, null!)).ParamName);
    }

    // Builds the chain of handlers for a named client, as IHttpClientFactory does, and returns its RetryHandler.
    private static RetryHandler BuildChain(IServiceProvider provider, string name)
    {
        HttpClientFactoryOptions options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        HttpMessageHandlerBuilder builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = name;
        foreach (Action<HttpMessageHandlerBuilder> action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return Assert.ContainsSingle(builder.AdditionalHandlers.OfType<RetryHandler>());
    }

    private static RetryTaskFactory<HttpResponseMessage> TransientFactory(TimeProvider timeProvider, CircuitBreaker? breaker)
    {
        return new RetryTaskFactory<HttpResponseMessage>(new Count(2), RetryPolicies.TransientHttp)
        {
            CanRetryResult = RetryPolicies.IsTransientHttpResponse,
            CircuitBreaker = breaker,
            TimeProvider = timeProvider,
        };
    }

    public sealed class CatalogueClient(HttpClient client)
    {
        public Task<string> GetStockAsync() => client.GetStringAsync(new Uri("products/42", UriKind.Relative));
    }
}
