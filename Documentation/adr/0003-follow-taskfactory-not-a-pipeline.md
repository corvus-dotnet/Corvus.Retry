# Follow TaskFactory, not a pipeline

## Status

Accepted.

## Context

Version 3.0 adds timeouts, result retries, a circuit breaker, a fallback and hedging. The first draft of 3.0 gave these features an API that copied Polly 8:

- `ReliabilityPipeline` and `ReliabilityPipelineOptions` copied `ResiliencePipeline` and its options.
- The draft also used the Polly names `ExecuteAsync`, `Handle<T>`, `FailureRatio`, `MinimumThroughput`, `BrokenCircuitException` and others.

No version with this API was released.

The goal is that applications can replace Polly with Corvus.Retry. But the library must look like Corvus.Retry, not like a copy of Polly. Corvus.Retry already had its own idioms:

- Calls start with a verb: `Retriable.Retry`, `RetryTask.Factory.StartNew`.
- Two concepts control every retry: `IRetryPolicy` decides whether an exception can cause a retry, and `IRetryStrategy` decides when.
- `RetryTask` and `RetryTaskFactory` follow the `Task` types of .NET.

## Decision

We will follow the `TaskFactory` of .NET, not a pipeline.

- A configured `RetryTaskFactory` is the reusable entry point. It has the same role as `new TaskFactory(cancellationToken, options, scheduler)`: the caller makes it one time, keeps it in a field, and calls it many times.
- `Run` runs an asynchronous operation and retries it, as `Task.Run` runs an operation. `RunValueTask` does the same for a `ValueTask`. It has a different name, because one name for the two overloads gives the error CS0121 for an `async` lambda.
- `RetryTask.Run` runs an operation with the default factory. `StartNew` stays, with all its 2.x overloads.
- Hedging is not a retry. It gets sibling types: `HedgedTask` and `HedgedTaskFactory`. Thus, a factory cannot have both hedging and a retry strategy, and no setting is necessary to forbid that combination.
- `IRetryPolicy` and `IRetryStrategy` stay the two core concepts. A factory takes them in its constructor.
- `init` properties and constructor parameters replace the options records. Each `init` accessor examines its own value. A copy constructor replaces the `with` expression. `CircuitBreaker` takes its settings as constructor parameters, because it reads the clock of its `TimeProvider` when it is constructed. `Fallback<T>` takes its action as a constructor parameter.
- `Run` keeps the behaviour of the draft pipeline. It throws the exception of the last attempt, and runs attempt 1 on the caller's thread until its first `await`. `StartNew` keeps its 2.x behaviour. Its task faults with an `AggregateException`, and its loop queues each try to a scheduler. `StartNew` throws an `InvalidOperationException` if the factory has a setting that only `Run` can use.
- We renamed the Polly names that are not general terms. For example, `ExecuteAsync` became `Run`, `Handle<T>` became `For<T>`, `ShouldRetryResult` became `CanRetryResult` (after `IRetryPolicy.CanRetry`), and `BrokenCircuitException` became `CircuitRejectedException`.
- We kept the names that are general vocabulary, or that Corvus.Retry used before: `CircuitBreaker`, `CircuitState`, `Isolate`, `Close`, `OnStateChanged`, `OnRetry`, `OnFallback`, `AttemptNumber`, `RetryAfter`, `ContinueOnCapturedContext`, `RetryTimeoutException` and `TransientHttp`.

## Consequences

- One type has two failure shapes. `Run` throws the exception of the last attempt. `StartNew` gives a task that faults with an `AggregateException` of the failures. The documentation tells which method does which. We did not make them the same, because that would change `StartNew` for 2.x users.
- The library has two retry loops. `Run` uses the retry engine. `StartNew` keeps its continuation-based loop. A merge of the two loops could change the behaviour of `StartNew`.
- `Run` is not the same as `Task.Run`. `Run` runs attempt 1 on the caller's thread, gives a token to the operation, and has no overload for a synchronous function. `ReliableTaskRunner.Run` is a third meaning of the word. The documentation tells these differences.
- The README describes the Corvus.Retry API only. A separate guide, [Migrating from Polly](../Migrating-from-Polly.md), maps the Polly names to the Corvus.Retry names.
- A user of 2.x keeps `Retriable`, `RetryTask.Factory.StartNew` and `ReliableTaskRunner`. The new functions are additions to types that the user knows.
