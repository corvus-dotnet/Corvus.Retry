# Migrating from Corvus.Retry 2.x to 3.0

Version 3.0 has changes that are not compatible with version 2.x. This guide gives each change, and the 3.0 replacement.

Do the steps in this guide before you do the upgrade. If you do not do them, your project can fail to compile, or your retries can operate differently.

The [CHANGELOG](../CHANGELOG.md) gives the new functions of version 3.0. The [README](../README.md) tells how to use them.

## Summary of the changes to the API

The .NET package validation tool compared the 3.0 package with the 2.0.0 package. This table gives each change that the tool found:

| 2.x item | Tool code | 3.0 replacement |
|----------|-----------|-----------------|
| The `netstandard2.0` target framework | PKV006 | None. Version 3.0 targets `net10.0` only. On a different framework, use version 2.x. See [Target framework](#target-framework). |
| The `Corvus.Retry.Async.ISleepService` interface | CP0001 | `TimeProvider`. See [Removed types](#removed-types). |
| The `Corvus.Retry.Strategies.RetryStrategy` base class | CP0001 | Implement `IRetryStrategy` directly. See [Strategies](#strategies). |
| `Count`, `DoNotRetry`, `Linear`, `Incremental` and `Backoff` derive from `RetryStrategy` | CP0007 | These classes now implement `IRetryStrategy` directly. See [Strategies](#strategies). |
| `IRetryStrategy.CanRetry` | CP0002 | `IRetryStrategy.GetRetryDelay` returns `null` when the strategy stops the retries. |
| `IRetryStrategy.PrepareToRetry(Exception)` | CP0002 | `IRetryStrategy.GetRetryDelay(in RetryAttempt)`. |
| `IRetryStrategy.Exception` | CP0002 | None. A strategy does not record exceptions. `RetryTask` records them. |
| `IRetryStrategy.OnRetrying(RetryEventArgs)` | CP0002 | The `onRetry` parameter of `Retriable`, or `ReliabilityPipelineOptions.OnRetry`. See [Events and callbacks](#events-and-callbacks). |
| The `IRetryStrategy.Retrying` event | CP0002 | The `onRetry` parameter of `Retriable`, or `ReliabilityPipelineOptions.OnRetry`. See [Events and callbacks](#events-and-callbacks). |
| A new member `IRetryStrategy.GetRetryDelay(in RetryAttempt)` | CP0006 | Implement this method in each custom strategy. See [Strategies](#strategies). |
| The `RetryEventArgs(Exception, TimeSpan)` constructor | CP0002 | `RetryEventArgs(Exception? lastException, object? result, TimeSpan delayBeforeRetry, int attemptNumber)`. |
| The setters of `Backoff.MinBackoff` and `Backoff.MaxBackoff` | CP0002 | `init` accessors. Set the values with an object initializer. |
| `Retriable.Retry<T>(Func<T>, CancellationToken, IRetryStrategy, IRetryPolicy)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.Retry(Action, CancellationToken, IRetryStrategy, IRetryPolicy)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.RetryAsync<T>(Func<Task<T>>, CancellationToken, IRetryStrategy, IRetryPolicy, bool)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.RetryAsync(Func<Task>, CancellationToken, IRetryStrategy, IRetryPolicy, bool)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |

The tool also gave CP0003 for the assembly version. This code occurs only in a local build without a version number. The release build has the version 3.0.0.0.

The tool does not find the changes in behavior, or the name of a parameter. The sections that follow also give these changes.

## Target framework

Version 3.0 targets `net10.0` only. Version 2.0 targeted `netstandard2.0` and `net10.0`.

- If your project targets `net10.0` or a later version, you can use version 3.0.
- If your project targets a different framework, continue to use version 2.x.

## Strategies

A strategy has no state for a call in version 3.0. Thus, many calls can use one strategy at the same time.

`IRetryStrategy` has one method:

```csharp
TimeSpan? GetRetryDelay(in RetryAttempt attempt);
```

The method returns the delay before the next attempt, or `null` to stop the retries. `RetryAttempt` gives the data about the attempt that failed:

- `AttemptNumber` is the number of the attempt that failed. The first attempt is 1.
- `Exception` is the exception of the attempt.
- `Result` is the result that caused the retry, or `null`. Only `ReliabilityPipeline<T>` retries results.
- `PreviousDelay` is the delay before the attempt that failed. It is zero for the first attempt.

To change a custom strategy:

1. Remove the base class `RetryStrategy`. Implement `IRetryStrategy`.
2. Move the code of `PrepareToRetry` and `CanRetry` into `GetRetryDelay`.
3. Remove the fields that count the attempts. Use `attempt.AttemptNumber`.
4. Remove the calls to `AddException`. The strategy does not record exceptions.
5. Remove all other state for a call from the strategy.

This example shows a 2.x strategy and the equivalent 3.0 strategy:

```csharp
// 2.x
public class MyCount : RetryStrategy
{
    private readonly int maxTries;
    private int tryCount;

    public MyCount(int maxTries) => this.maxTries = maxTries;

    public override bool CanRetry => this.tryCount < this.maxTries;

    public override TimeSpan PrepareToRetry(Exception lastException)
    {
        this.AddException(lastException);
        this.tryCount++;
        return TimeSpan.Zero;
    }
}

// 3.0
public class MyCount : IRetryStrategy
{
    private readonly int maxTries;

    public MyCount(int maxTries) => this.maxTries = maxTries;

    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        return attempt.AttemptNumber < this.maxTries ? TimeSpan.Zero : null;
    }
}
```

These changes also apply to the built-in strategies:

- **One instance for many calls.** In version 2.x, a strategy kept its count between calls. Thus, a second call with the same instance could get fewer tries. In version 3.0, each call gets all its tries.
- **`Backoff` bounds.** `MinBackoff` and `MaxBackoff` have `init` accessors. Set them with an object initializer: `new Backoff(5, TimeSpan.FromSeconds(1)) { MaxBackoff = TimeSpan.FromSeconds(10) }`. You cannot change them after you make the strategy.
- **`Incremental` parameter name.** The constructor parameter `intialDelay` is now `initialDelay`. If you use the named argument `intialDelay:`, change it to `initialDelay:`.
- **Exceptions for incorrect arguments.** A `maxTries` value of zero or less now gives an `ArgumentOutOfRangeException`, not an `ArgumentException`. A negative delay (`periodicity`, `initialDelay`, `step` or `deltaBackoff`) also gives an `ArgumentOutOfRangeException`. `ArgumentOutOfRangeException` derives from `ArgumentException`, thus a `catch (ArgumentException)` block continues to operate.
- **Maximum delay.** Each strategy limits its delays to 0xFFFFFFFE ms (approximately 49.7 days). `Backoff` and `Incremental` do not overflow with a large number of tries.

## Events and callbacks

Version 3.0 has no `Retrying` event and no `OnRetrying` method. Use a callback:

| 2.x | 3.0 |
|-----|-----|
| `strategy.Retrying += handler` with `Retriable` | The `onRetry` parameter of the `Retriable` overloads that have all the parameters, or `ReliabilityPipelineOptions.OnRetry` if you move the call to a `ReliabilityPipeline` |
| `strategy.Retrying += handler` with `RetryTask` | None. `RetryTask` has no retry callback. Use `Retriable` or `ReliabilityPipeline` in the operation, if you must have a callback. |

The callback is `Func<RetryEventArgs, ValueTask>`. `Retriable` and the pipeline await it before the delay. An exception from the callback stops the call. The synchronous `Retry` overloads wait for the callback.

`RetryEventArgs` has these changes:

- The constructor is `RetryEventArgs(Exception? lastException, object? result, TimeSpan delayBeforeRetry, int attemptNumber)`.
- `LastException` can be `null`. It is `null` when a result caused the retry.
- The new property `Result` gives that result.
- The new property `AttemptNumber` gives the number of the attempt that failed. The first attempt is 1.
- The new property `NextAttemptNumber` gives the number of the attempt that starts after the delay.

The `onRetry` parameter is optional. Thus, your source code compiles without a change. But the signatures of the four overloads changed. Thus, you must compile your project again against version 3.0. A library that you compiled against version 2.x can fail with `MissingMethodException`.

## Cancellation

The `Retriable` overloads that have a cancellation token now obey these rules:

| Condition | 2.x | 3.0 |
|-----------|-----|-----|
| The token is cancelled before the call | `Retriable` called the operation one time. | `Retriable` does not call the operation. It throws an `OperationCanceledException`. |
| The token is cancelled after a failure | `Retriable` rethrew the exception of the operation. | `Retriable` throws an `OperationCanceledException`. |
| The token is cancelled during a delay | The delay continued until its end. | The delay stops immediately. `Retriable` throws an `OperationCanceledException`. |
| The operation throws an `OperationCanceledException` that the token caused | `Retriable` gave the exception to the strategy, and then rethrew it. | `Retriable` rethrows it, and does not give it to the strategy or the policy. |

If your code catches the exception of the operation after a cancellation, change it to also catch `OperationCanceledException`.

The overloads without a cancellation token use `CancellationToken.None`. Thus, these rules have no effect on them.

## RetryTask

`RetryTask.Factory` and `RetryTask<T>.Factory` keep all their `StartNew` overloads.

- A retry does not block a thread. In version 2.x, the delay blocked a thread, and each retry ran synchronously on that thread. In version 3.0, a delay task and continuations start each retry on the scheduler of the caller.
- `RetryTask` has no retry callback, because the `Retrying` event is removed.
- If the scheduler refuses to queue a retry, the refusal is a failure of that try. The strategy and the policy decide if `RetryTask` retries it. In version 2.0, the task faulted immediately.
- If the operation stops, the task faults with all the failures that the operation recorded. This did not change.

## ReliableTaskRunner

`ReliableTaskRunner` now implements `IAsyncDisposable`.

- Use `await using` to stop the runner at the end of a scope. `DisposeAsync` stops the operation, waits until the run ends, and releases the `CancellationTokenSource`. It does not throw the exception of the run. A second call has no effect.
- `StopAsync` cancels the token with `CancelAsync`. Thus, the callbacks of the token run on a thread-pool thread, not on the thread that calls `StopAsync`. In version 2.x, an exception from a callback came from `StopAsync` immediately. In version 3.0, the task from `StopAsync` faults with that exception.
- `StopAsync` returns a new task, not the task of the run. The task ends as the last run ended. If the run faulted with many exceptions, the task faults with all of them.
- After `DisposeAsync`, `StopAsync` does not cancel the token. Its task ends as the last run ended.

## Removed types

| 2.x type | 3.0 replacement |
|----------|-----------------|
| `ISleepService` and `SleepService` | `TimeProvider`. `Retriable` and `RetryTask` use `TimeProvider.System`. `ReliabilityPipeline` and `CircuitBreaker` take a `TimeProvider` in their options. See [ADR 0002](adr/0002-use-timeprovider-for-delays-and-timeouts.md). |
| `RetryStrategy` | Implement `IRetryStrategy`. See [Strategies](#strategies). |

`SleepService` was internal in version 2.x. Only the tests of this project used `ISleepService`.

## New public members of 2.x types

These changes are compatible with version 2.x. They can cause a conflict with an extension method of the same name in your project:

- `AnyExceptionPolicy.Default` is a shared instance.
- `RetryPolicyExtensions` is public. It has the extension members `Or`, `And` and `Not` for `IRetryPolicy`.
- `RetryStrategies.Default` is the shared default strategy of `Retriable` (`Count(10)`).
- `Retriable` has new overloads for `Func<CancellationToken, Task<T>>` and `Func<CancellationToken, Task>`.
