# Use TimeProvider for delays and timeouts

## Status

Accepted. This ADR replaces [ADR 0001](0001-centralised-access-to-isleepservice.md).

## Context

Version 2.0 used the `ISleepService` interface for all delays between retries.

- `ISleepService` was an internal test seam. Only the tests of this project replaced it.
- The `SleepService` static class held the instance for the full process.
- The default service used `Thread.Sleep` for synchronous delays and `Task.Delay` for asynchronous delays.

.NET 8 and later versions have the `TimeProvider` abstract class. `TimeProvider` gives the time, timers and delays. `Task.Delay` and `CancellationTokenSource` accept a `TimeProvider`.

Version 3.0 adds timeouts and a circuit breaker. A test must control the time for these features too. `ISleepService` controls only delays. It cannot control a timeout or a time measurement.

The package `Microsoft.Extensions.TimeProvider.Testing` has the `FakeTimeProvider` class. With `FakeTimeProvider`, a test moves the time forward with `Advance`. Thus, a test of a delay of 1 hour takes less than 1 second.

## Decision

We will use `TimeProvider` for all delays and all timeouts.

- We will delete `ISleepService` and `SleepService`.
- `Retriable` and `RetryTaskFactory` have no options object. They read the internal property `RetryDefaults.TimeProvider` for each call. The default value is `TimeProvider.System`.
- `ReliabilityPipeline` and `CircuitBreaker` get the `TimeProvider` from their options. They do not read `RetryDefaults.TimeProvider`.
- An asynchronous delay uses `Task.Delay(delay, timeProvider, token)`.
- A synchronous delay waits for a timer from `TimeProvider`. If the token cannot be cancelled and the provider is `TimeProvider.System`, the delay uses `Thread.Sleep`.
- A zero delay does not create a timer.
- The tests use `FakeTimeProvider` to control the time.

## Consequences

- One abstraction controls delays, timeouts and time measurements. This abstraction is part of .NET.
- A caller of `ReliabilityPipeline` can give a `TimeProvider` in the options. Thus, the tests of an application can control the time of its retries.
- The tests of this project do not wait for the system clock.
- When we delete `ISleepService`, we make a breaking change. But only the tests of this project used it.
- `RetryDefaults.TimeProvider` is a value for the full process. A test that changes it must not run in parallel with other tests. The test must set the value back to `TimeProvider.System` when it ends.
- The test project has a dependency on `Microsoft.Extensions.TimeProvider.Testing`. The library does not have this dependency.
