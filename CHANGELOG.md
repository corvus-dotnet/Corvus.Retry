# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

These changes are planned for 3.0.0.

Version 3.0 is not compatible with version 2.x. Read the
[migration guide from 2.x to 3.0](Documentation/Migrating-from-2.x-to-3.0.md) before you do the upgrade. If you do
not do the steps in the guide, your project can fail to compile, or your retries can operate differently.

### Added

- `RetryPipeline` holds all the retry settings in one immutable object. Many threads can use one pipeline at the same
  time. Its `ExecuteAsync` methods give a cancellation token to the operation, and use `ValueTask`.
- `RetryPipelineOptions` is an immutable record with the settings of a pipeline. The constructor of the pipeline
  examines the options. The `ParamName` of each exception is the name of the incorrect property.
- `RetryPipeline<T>` and `RetryPipelineOptions<T>` can retry a result. If `ShouldRetryResult` returns `true`, the
  pipeline uses the result as a failure.
- `AttemptTimeout` limits the time of each attempt. `TotalTimeout` limits the time of all the attempts and all the
  delays.
- `RetryTimeoutException` derives from `TimeoutException`. A pipeline throws it when a timeout expires. Its `Timeout`
  property gives the time limit.
- `RetryPipelineOptions<T>.Fallback` gives a value when the pipeline fails. `FallbackOptions<T>` and
  `FallbackArgs<T>` hold its settings and its data.
- `CircuitBreaker` stops calls to a service that fails frequently. Many pipelines can share one breaker.
  `CircuitBreakerOptions`, `CircuitState`, `CircuitStateChange`, `BrokenCircuitException` and
  `IsolatedCircuitException` are new. `Isolate()` and `Close()` control the circuit manually.
- `RetryPipelineOptions.Hedging` starts more attempts in parallel when an attempt is slow or fails.
  `HedgingOptions`, `HedgingEventArgs` and `RetryPipelineOptions<T>.HedgingActionGenerator` are new.
- The `Exponential` strategy doubles the delay before each retry. With jitter, it uses decorrelated jitter.
- `Linear` and `Incremental` have new constructor overloads with the parameters `maxDelay` and `useJitter`.
- `RetryAttempt` gives the data about an attempt that failed to a strategy. `AttemptNumber` starts at 1.
- The static class `RetryStrategies` makes strategies that count retries, not attempts: `Constant`, `Linear`,
  `Exponential`, `FromDelegate` and `RetryAfter`. `RetryStrategies.Default` is the shared default strategy of
  `Retriable`.
- `RetryStrategies.RetryAfter` uses the `Retry-After` header of an `HttpResponseMessage` result, or
  `BrokenCircuitException.RetryAfter`, for the delay.
- The static class `RetryPolicies` makes immutable policies: `FromPredicate`, `Handle<T>`, `HandleInner<T>`, `Not`,
  `Any` and `All`. `Handle<T>` returns an `ExceptionTypeRetryPolicy`, which has the method `Or<T>`.
- `RetryPolicies.TransientHttp` retries transient HTTP failures. `RetryPolicies.IsTransientHttpResponse` finds a
  transient HTTP status in a result.
- The C# 14 extension members `Or`, `And` and `Not` combine policies. `RetryPolicyExtensions` is now public.
- `AnyExceptionPolicy.Default` is a public shared instance.
- `Retriable` has overloads for `Func<CancellationToken, Task<T>>` and `Func<CancellationToken, Task>`. The token
  goes to the operation.
- The `Retriable` overloads that have all the parameters have an optional `onRetry` callback. The callback receives
  the attempt numbers.
- `RetryEventArgs` has the properties `Result`, `AttemptNumber` and `NextAttemptNumber`.
- `ReliableTaskRunner` implements `IAsyncDisposable`. Use `await using` to stop the runner at the end of a scope.
- The benchmarks measure the pipeline, the fallback, the circuit breaker, hedging, the new strategies and the new
  policies. `DelayBenchmarks` replaces `SleepBenchmarks`.
- A sample for `RetryPipeline` is in `Documentation/Examples/RetryPipelineSample.cs`.
- ADR 0002 records the decision to use `TimeProvider`. It replaces ADR 0001.

### Changed

- The package targets `net10.0` only. On a different framework, use version 2.x.
- `IRetryStrategy` has one method, `GetRetryDelay(in RetryAttempt)`. A strategy has no state for a call. Thus, many
  calls can use one strategy at the same time. A strategy that a call used before now gives all its tries to the next
  call.
- `RetryEventArgs` has a new constructor with an attempt number. `LastException` can be `null`, when a result caused
  the retry.
- The `Retriable` overloads that have a token examine the token before each attempt, also before the first attempt.
  CAUTION: A cancelled token now gives an `OperationCanceledException`. Before, `Retriable` called the operation one
  time, or rethrew the exception of the operation. If your code catches that exception, also catch
  `OperationCanceledException`.
- A cancellation token stops a `Retriable` delay immediately. Before, the delay continued until its end.
- `Retriable` and the pipelines do not give an `OperationCanceledException` that the token of the caller caused to
  the strategy or the policy.
- The synchronous `Retriable.Retry` overloads with a token that can be cancelled wait on a timer, not on
  `Thread.Sleep`.
- A retry of `RetryTask` does not block a thread. A delay task and continuations start each retry on the scheduler of
  the caller.
- If a scheduler refuses to queue a `RetryTask` retry, the refusal is a failure of that try. The strategy and the
  policy decide about it. Before, the task faulted immediately.
- `Backoff.MinBackoff` and `Backoff.MaxBackoff` have `init` accessors. Set them with an object initializer.
- The `Incremental` constructor parameter `intialDelay` is now `initialDelay`.
- An incorrect `maxTries` gives an `ArgumentOutOfRangeException`, not an `ArgumentException`. A negative delay
  parameter of a strategy also gives an `ArgumentOutOfRangeException`.
- Each strategy limits its delays to 0xFFFFFFFE ms (approximately 49.7 days). `Backoff` and `Incremental` do not
  overflow.
- The four `Retriable` overloads that have all the parameters have a new optional `onRetry` parameter. Your source
  code compiles without a change, but you must compile it again against this version.
- `ReliableTaskRunner.StopAsync` cancels with `CancelAsync`. The callbacks of the token run on a thread-pool thread.
  An exception from a callback faults the task from `StopAsync`. Before, `StopAsync` threw it immediately.
- `ReliableTaskRunner.StopAsync` returns a new task that ends as the last run ended. If the run faulted with many
  exceptions, the task faults with all of them.
- `Retriable` and `RetryTask` use `TimeProvider.System` for their delays.
- The 2.x benchmark methods `Retry_WithDelay_Sleep` and `RetryAsync_WithDelay_SleepAsync` are now
  `Retry_WithDelay_ThreadSleep` and `RetryAsync_WithDelay_TaskDelay` in `DelayBenchmarks`.
- `Retriable` allocates less memory than 2.x. With two failures, `Retry` allocates 520 B (2.x: 688 B). `RetryAsync`
  allocates 1,776 B (2.x: 1,944 B).
- A strategy allocates no memory for a call. For 9 retries, `Count` takes approximately 11 ns. 2.x took approximately
  95 ns and 368 B, because each call needed a new strategy.
- An asynchronous `Retriable` call that completes immediately takes approximately 3 ns to 22 ns more than in 2.x. The
  cause is the retry loop, which now also examines the token, the timeouts and the callbacks.

### Removed

- This release removes the `netstandard2.0` target. Version 2.x also targets `netstandard2.0`.
- This release removes `ISleepService` and `SleepService`. The library uses `TimeProvider`.
- This release removes the `RetryStrategy` base class. Implement `IRetryStrategy` directly.
- This release removes the members `CanRetry`, `PrepareToRetry`, `Exception` and `OnRetrying`, and the `Retrying`
  event, from `IRetryStrategy`. Use `GetRetryDelay`, and the `onRetry` callback of `Retriable` or the `OnRetry` option
  of the pipeline.
- `RetryTask` has no retry callback, because the `Retrying` event is removed.
- This release removes the `RetryEventArgs(Exception, TimeSpan)` constructor.
- This release removes the `RetryStrategyBenchmarks` and `SleepBenchmarks` benchmark classes.

## [2.0.0]

### Added

- A `net10.0` target, alongside `netstandard2.0`.
- A software bill of materials (SBOM) in Covenant, SPDX and CycloneDX formats, attached to each GitHub Release with
  the NuGet packages.
- Benchmarks for every public API, in `Solutions/Corvus.Retry.Benchmarks`.
- Samples as .NET 10 file-based apps, replacing the Polyglot notebooks.

### Changed

- `RetryTask` runs every retry on the caller's scheduler. Before, only the first try did; retry decisions and delays
  run on the thread pool.
- With `TaskCreationOptions.AttachedToParent`, `RetryTask` attaches the returned task to the parent, not each try.
  Tries that were later retried no longer fault the parent.
- `RetryTask` and `ReliableTaskRunner` ask the policy about the exceptions the operation threw, not the
  `AggregateException` around them. The policy is called once for each exception, depth first, and every one must be
  accepted for a retry or restart. An `AggregateException` with no inner exceptions is passed to the policy itself.
  A custom policy that tests for `AggregateException`, or counts how often it is called, needs updating.
- `RetryTask` reports cancellation as a `Canceled` task that carries the caller's token. In 1.x, cancelling during
  retries gave a task that completed successfully (for an action) or faulted (for a function).
- `RetryTask.Factory.StartNew` and `RetryTask<T>.Factory.StartNew` return a `Canceled` task when the token is
  already cancelled. In 1.x, the non-generic factory returned a task that completed successfully without running the
  action.
- When the policy has already refused a retry, `RetryTask` reports the failure even if the token is then cancelled.
- `ReliableTaskRunner` asks the policy what to do when a run cancels itself (for example an `HttpClient` timeout).
  With the default policy the run is restarted. In 1.x, the runner stopped silently.
- Null arguments throw `ArgumentNullException`. In 1.x, some overloads threw `NullReferenceException`.
- The 5-argument `Retriable.RetryAsync` overloads throw `ArgumentNullException` when called with a null argument,
  instead of returning a faulted task. Code that awaits the call directly sees no difference.
- An `AggregatePolicy` with no child policies allows a retry.
- The `state` parameters of the `RetryTask` factory overloads are annotated as `object?` (`Action<object?>` and
  `Func<object?, T>`), matching `TaskFactory.StartNew`.
- Lower time and allocation costs:
  - `Retriable.Retry` and `Retriable.RetryAsync` allocate less on every call.
  - Policy checks no longer allocate.
  - Strategies create their exception list on the first failure.
  - `Backoff` shares one random number generator instead of creating one per delay.
  - `RetryTask` retries allocate about 25% less.

### Fixed

- `RetryTask` lost the failure when the operation gave up. The returned task now faults with every failure the strategy
  recorded, or with the last try's exception if the strategy recorded none (for example with `DoNotRetry`). In 1.x,
  the task faulted with an empty `AggregateException`.
- `RetryTask` kept retrying after cancellation. It now stops, ends `Canceled` with the caller's token, stops waiting
  for the strategy's delay, and does not raise `Retrying` for a retry that will not run.
- `ReliableTaskRunner.StopAsync` during a restart could return the previous, faulted run while a new run kept going.
- `Retriable.RetryAsync` reset the stack trace of the exception it rethrew when it gave up.
- `RetryTask.Factory` and `RetryTask<T>.Factory` could give different instances to threads that accessed them for the
  first time at once.
- `AggregatePolicy.CanRetry` threw `NullReferenceException` if `Policies` had never been accessed.
- `Backoff` gave concurrent retries on .NET Framework the same jitter, because each delay created a `Random` with a
  time-based seed.

## [1.0.7] - 2023-05-03

### Changed

- Clarified the documentation of the `maxTries` parameter.
- Added Polyglot notebook samples for `Retriable`, `RetryTask` and `ReliableTaskRunner`.
- Updated dependencies.

## [1.0.6] - 2022-01-31

### Changed

- Updated to Endjin.RecommendedPractices 2.

## [1.0.5] - 2021-12-10

### Changed

- Enabled NuGet lock files.
- Updated dependencies.

## [1.0.4] - 2021-04-28

### Changed

- Updated dependencies.

## [1.0.3] - 2021-04-09

### Changed

- Updated dependencies.

## [1.0.2] - 2021-03-16

### Changed

- Updated dependencies.

## [1.0.1] - 2021-02-11

### Changed

- Updated dependencies.

## [1.0.0] - 2020-07-13

### Changed

- First stable release. The API is unchanged from 0.5.0.

## [0.5.0] - 2020-07-09

### Added

- Nullable reference type annotations.
- Unit tests.

### Changed

- Updated to SpecFlow 3.3 and Endjin.RecommendedPractices.

## [0.4.0] - 2019-09-06

### Changed

- Updated dependencies.

## [0.3.0] - 2019-09-06

### Added

- Basic argument validation.

## [0.2.0] - 2019-08-21

### Changed

- Build and release process changes only.

## [0.1.1] - 2019-08-21

### Changed

- Build and release process changes only.

## [0.1.0] - 2019-08-21

### Added

- First release: `Retriable`, `RetryTask`, `ReliableTaskRunner`, retry strategies and retry policies.

[Unreleased]: https://github.com/corvus-dotnet/Corvus.Retry/compare/2.0.0...HEAD
[2.0.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.7...2.0.0
[1.0.7]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.6...1.0.7
[1.0.6]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.5...1.0.6
[1.0.5]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.4...1.0.5
[1.0.4]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.3...1.0.4
[1.0.3]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.2...1.0.3
[1.0.2]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.1...1.0.2
[1.0.1]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.0...1.0.1
[1.0.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.5.0...1.0.0
[0.5.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.4.0...0.5.0
[0.4.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.3.0...0.4.0
[0.3.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.2.0...0.3.0
[0.2.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.1.1...0.2.0
[0.1.1]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.1.0...0.1.1
[0.1.0]: https://github.com/corvus-dotnet/Corvus.Retry/releases/tag/0.1.0
