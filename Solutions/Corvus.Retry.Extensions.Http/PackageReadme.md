# Corvus.Retry.Extensions.Http

This package adds the `RetryHandler` of [Corvus.Retry](https://www.nuget.org/packages/Corvus.Retry) to an `HttpClient`
that `IHttpClientFactory` makes. Your code calls `HttpClient` as usual, and each request gets the retries, timeouts,
circuit breaker and fallback of one `RetryTaskFactory<HttpResponseMessage>`.

This package targets `net10.0`, and depends on `Corvus.Retry` and `Microsoft.Extensions.Http`.

## Usage

```csharp
using Corvus.Retry;
using Corvus.Retry.Http;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

// Make the factory once. Every handler that IHttpClientFactory builds uses it, and shares its circuit breaker.
RetryTaskFactory<HttpResponseMessage> retries = new(
    RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(1), useJitter: true)),
    RetryPolicies.TransientHttp)
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(5),
    TotalTimeout = TimeSpan.FromSeconds(30),
};

services.AddHttpClient<CatalogueClient>().AddRetryHandler(retries);
```

The handler retries GET, HEAD, PUT, DELETE, OPTIONS and TRACE requests whose content can be sent again. Other
requests, such as POST, get one attempt. To retry a POST, pass a predicate:
`AddRetryHandler(retries, RetryHandler.CanReplayContent)`.

## Limits

- The handler returns when the headers of a response arrive. `HttpClient` reads a buffered body after that, so a
  failure while the body downloads is not retried, and the attempt timeout does not limit it. To retry the body too,
  use `retries.Run(...)` around the whole call.
- `HttpClient.Timeout` (100 seconds by default) limits all the attempts and delays together, and it throws a
  `TaskCanceledException`. Make the `TotalTimeout` of the factory shorter.
- The handler does not support the synchronous `HttpClient.Send`, and it does not hedge.

## More information

- [Documentation and samples](https://github.com/corvus-dotnet/Corvus.Retry#readme)
- [A runnable demo of the handler, with a named and a typed client](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/Examples/http-handler.cs)
- [Changelog](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/CHANGELOG.md)
- [Issues](https://github.com/corvus-dotnet/Corvus.Retry/issues)

Corvus.Retry is sponsored by [endjin](https://endjin.com) and available under the Apache 2.0 licence.
