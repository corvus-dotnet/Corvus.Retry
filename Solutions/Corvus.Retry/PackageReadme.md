# Corvus.Retry

Retry support for synchronous and asynchronous operations, and a self-restarting host for long-running tasks, with
pluggable retry policies and strategies.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

## Usage

`Retriable` wraps a synchronous or asynchronous operation, and runs it again if it throws:

```csharp
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

SomeResult result = Retriable.Retry(() => GetSomething());
await Retriable.RetryAsync(() => DoSomethingAsync());

// Up to 5 tries, 1 second apart. Pass your own IRetryPolicy to retry only some exceptions.
SomeResult result2 = Retriable.Retry(
    () => GetSomething(),
    CancellationToken.None,
    new Linear(TimeSpan.FromSeconds(1), 5),
    new AnyExceptionPolicy());
```

`RetryTask.Factory` and `RetryTask<T>.Factory` work like `Task.Factory`, with retry:

```csharp
Task<SomeResult> task = RetryTask<SomeResult>.Factory.StartNew(() => GetSomething(), new Count(5), new AnyExceptionPolicy());
```

`ReliableTaskRunner` keeps a long-running operation going, restarting it after a failure, until you stop it:

```csharp
ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => ProcessMessagesAsync(cancellationToken));
await runner.StopAsync();
```

- **Policies** (`IRetryPolicy`) decide whether a failure is retried: `AnyExceptionPolicy`, `DoNotRetryPolicy`,
  `AggregatePolicy`, or your own.
- **Strategies** (`IRetryStrategy`) decide how many tries are made and the delay between them: `Count`, `DoNotRetry`,
  `Linear`, `Incremental` and `Backoff`, or your own.

## More information

- [Documentation and samples](https://github.com/corvus-dotnet/Corvus.Retry#readme)
- [Changelog](https://github.com/corvus-dotnet/Corvus.Retry/blob/main/CHANGELOG.md), including the behaviour changes in
  2.0
- [Issues](https://github.com/corvus-dotnet/Corvus.Retry/issues)

Corvus.Retry is sponsored by [endjin](https://endjin.com) and available under the Apache 2.0 license.
