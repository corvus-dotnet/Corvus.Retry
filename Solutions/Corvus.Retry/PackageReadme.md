# Corvus.Retry

This library retries synchronous and asynchronous operations that fail. It also restarts long-running tasks that fail.
A policy (`IRetryPolicy`) decides whether an exception can cause a retry. A strategy (`IRetryStrategy`) decides when:
the number of attempts and the delay before each retry.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

## Usage

`Retriable` puts retries around one call:

```csharp
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

SomeResult result = Retriable.Retry(() => GetSomething());
await Retriable.RetryAsync(ct => DoSomethingAsync(ct), cancellationToken);
```

`RetryTask.Run` runs an asynchronous operation with the default settings (5 attempts, no delay):

```csharp
string body = await RetryTask.Run(ct => httpClient.GetStringAsync(uri, ct), cancellationToken);
```

A configured `RetryTaskFactory` keeps the settings for many calls, as `new TaskFactory(...)` does. Make one and keep it
in a field. It adds timeouts, result retries, a fallback and a circuit breaker:

```csharp
RetryTaskFactory<HttpResponseMessage> factory = new(
    RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200))),
    RetryPolicies.TransientHttp)
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(5),
    TotalTimeout = TimeSpan.FromSeconds(30),
};

using HttpResponseMessage response = await factory.Run(ct => httpClient.GetAsync(uri, ct), cancellationToken);
```

Use `Run` for a `Task`, `RunValueTask` for a `ValueTask`, and `StartNew` for a synchronous function, as
`Task.Factory.StartNew` takes.

The package also has:

- `HedgedTaskFactory`, which starts more attempts in parallel when an attempt is slow or fails.
- `CircuitBreaker`, which stops calls to a service that fails frequently.
- `RetryHandler`, which applies a factory to each request of an `HttpClient`. For `IHttpClientFactory`, the package
  `Corvus.Retry.Extensions.Http` has `AddRetryHandler`.
- `ReliableTaskRunner`, which runs a long-running operation until you stop it, and starts it again if it fails.

## More information

- [Documentation and samples](https://github.com/corvus-dotnet/Corvus.Retry#readme)
- [Changelog](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/CHANGELOG.md)
- [Migration guide from 2.x to 3.0](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/Documentation/Migrating-from-2.x-to-3.0.md)
- [Migration guide from Polly](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/Documentation/Migrating-from-Polly.md)
- [Issues](https://github.com/corvus-dotnet/Corvus.Retry/issues)

Corvus.Retry is sponsored by [endjin](https://endjin.com) and available under the Apache 2.0 licence.
