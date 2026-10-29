# Corvus.Retry

This library retries synchronous and asynchronous operations that fail. It also restarts long-running tasks that fail.
Policies and strategies control the retries.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

## Usage

`ReliabilityPipeline` holds all the retry settings in one immutable object. Make one pipeline and keep it in a field. The
pipeline gives a cancellation token to the operation, and cancels it when the caller cancels or a timeout expires:

```csharp
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    Policy = RetryPolicies.TransientHttp,
    AttemptTimeout = TimeSpan.FromSeconds(5),
    TotalTimeout = TimeSpan.FromSeconds(30),
});

string body = await pipeline.ExecuteAsync(
    ct => new ValueTask<string>(httpClient.GetStringAsync(uri, ct)),
    cancellationToken);
```

A pipeline can also retry a result (`ReliabilityPipeline<T>`), and can have a fallback, a circuit breaker and hedging.

`Retriable` puts retries around a synchronous or an asynchronous operation:

```csharp
SomeResult result = Retriable.Retry(() => GetSomething());
await Retriable.RetryAsync(ct => DoSomethingAsync(ct), cancellationToken);

// Up to 5 tries, 1 second apart. Give your own IRetryPolicy to retry only some exceptions.
SomeResult result2 = Retriable.Retry(
    () => GetSomething(),
    CancellationToken.None,
    new Linear(TimeSpan.FromSeconds(1), 5),
    AnyExceptionPolicy.Default);
```

`RetryTask.Factory` and `RetryTask<T>.Factory` operate as `Task.Factory` does, with retries:

```csharp
Task<SomeResult> task = RetryTask<SomeResult>.Factory.StartNew(() => GetSomething(), new Count(5), AnyExceptionPolicy.Default);
```

`ReliableTaskRunner` runs a long-running operation until you stop it. If the operation fails, the runner starts it
again:

```csharp
await using ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => ProcessMessagesAsync(cancellationToken));
```

- **Policies** (`IRetryPolicy`) decide if a failure can cause a retry: `AnyExceptionPolicy`, `DoNotRetryPolicy`,
  `AggregatePolicy`, the `RetryPolicies` methods (`Handle<T>`, `HandleInner<T>`, `FromPredicate`, `Not`, `Any`, `All`,
  `TransientHttp`), or your own policy.
- **Strategies** (`IRetryStrategy`) decide the number of tries and the delay before each retry: `Count`, `DoNotRetry`,
  `Linear`, `Incremental`, `Backoff`, `Exponential`, the `RetryStrategies` methods (`Constant`, `Linear`,
  `Exponential`, `FromDelegate`, `RetryAfter`), or your own strategy.

## More information

- [Documentation and samples](https://github.com/corvus-dotnet/Corvus.Retry#readme)
- [Changelog](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/CHANGELOG.md)
- [Migration guide from 2.x to 3.0](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/Documentation/Migrating-from-2.x-to-3.0.md)
- [Issues](https://github.com/corvus-dotnet/Corvus.Retry/issues)

Corvus.Retry is sponsored by [endjin](https://endjin.com) and available under the Apache 2.0 licence.
