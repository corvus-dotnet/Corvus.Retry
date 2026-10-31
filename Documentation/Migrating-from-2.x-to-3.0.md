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
| The `RetryEventArgs(Exception, TimeSpan)` constructor | CP0002 | `RetryEventArgs(Exception? LastException, object? Result, TimeSpan DelayBeforeRetry, int AttemptNumber)`. |
| `RetryEventArgs` is a class that derives from `EventArgs` | CP0007, CP0009 | `RetryEventArgs` is a `readonly record struct`. See [Events and callbacks](#events-and-callbacks). |
| `RetryTaskFactory` and `RetryTaskFactory<T>` are not sealed | CP0009 | Both classes are sealed. If you derived a class from them, call `RetryTask.Factory` from a class of your own. |
| The setters of `Backoff.MinBackoff` and `Backoff.MaxBackoff` | CP0002 | `init` accessors. Set the values with an object initialiser. |
| `Retriable.Retry<T>(Func<T>, CancellationToken, IRetryStrategy, IRetryPolicy)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.Retry(Action, CancellationToken, IRetryStrategy, IRetryPolicy)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.RetryAsync<T>(Func<Task<T>>, CancellationToken, IRetryStrategy, IRetryPolicy, bool)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |
| `Retriable.RetryAsync(Func<Task>, CancellationToken, IRetryStrategy, IRetryPolicy, bool)` | CP0002 | The same overload with an optional `onRetry` parameter. Compile your project again. |

The tool also gave CP0003 for the assembly version. This code occurs only in a local build without a version number. The release build has the version 3.0.0.0.

The tool does not find the changes in behaviour, or the name of a parameter. The sections that follow also give these changes.

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
- **`Backoff` bounds.** `MinBackoff` and `MaxBackoff` have `init` accessors. Set them with an object initialiser: `new Backoff(5, TimeSpan.FromSeconds(1)) { MaxBackoff = TimeSpan.FromSeconds(10) }`. You cannot change them after you make the strategy. A negative value gives an `ArgumentOutOfRangeException`. If `MinBackoff` is greater than `MaxBackoff`, `GetRetryDelay` throws an `ArgumentException`. In version 2.x, the minimum was ignored.
- **`Backoff` jitter.** The random part of the delay is no longer rounded down to whole milliseconds, and stays within ±20 % of `deltaBackoff` for every delta. In version 2.x, a delta of a few milliseconds gave a constant value.
- **`RetryAttempt.AttemptNumber`.** An attempt number less than 1 gives an `ArgumentOutOfRangeException`. A custom engine must number attempts from 1.
- **`Incremental` parameter name.** The constructor parameter `intialDelay` is now `initialDelay`. If you use the named argument `intialDelay:`, change it to `initialDelay:`.
- **Exceptions for incorrect arguments.** A `maxTries` value of zero or less now gives an `ArgumentOutOfRangeException`, not an `ArgumentException`. A negative delay (`periodicity`, `initialDelay`, `step` or `deltaBackoff`) also gives an `ArgumentOutOfRangeException`. `ArgumentOutOfRangeException` derives from `ArgumentException`, thus a `catch (ArgumentException)` block continues to operate.
- **Maximum delay.** Each strategy limits its delays to 0xFFFFFFFE ms (approximately 49.7 days). `Backoff` and `Incremental` do not overflow with a large number of tries.

## Events and callbacks

Version 3.0 has no `Retrying` event and no `OnRetrying` method. Use a callback:

| 2.x | 3.0 |
|-----|-----|
| `strategy.Retrying += handler` with `Retriable` | The `onRetry` parameter of the `Retriable` overloads that have all the parameters, or `ReliabilityPipelineOptions.OnRetry` if you move the call to a `ReliabilityPipeline` |
| `strategy.Retrying += handler` with `RetryTask` | None. `RetryTask` has no retry callback. Use `Retriable` or `ReliabilityPipeline` in the operation, if you must have a callback. |

The callback is `Func<RetryEventArgs, ValueTask>`. `Retriable` and the pipeline await it before the delay. An exception from the callback stops the call. The synchronous `Retry` overloads wait for the callback. They run the callback with no `SynchronizationContext`, and with `TaskScheduler.Default` as the current scheduler. Thus, if the callback awaits on a UI thread, or on a scheduler that runs one task at a time, it continues on the thread pool, and a deadlock does not occur.

`RetryEventArgs` has these changes:

- `RetryEventArgs` is a `readonly record struct`, not a class. It does not derive from `EventArgs`. Thus, you cannot compare it with `null`, and you cannot derive a class from it. To keep the last value in a variable, use `RetryEventArgs?`.
- The constructor is `RetryEventArgs(Exception? LastException, object? Result, TimeSpan DelayBeforeRetry, int AttemptNumber)`. If you use named arguments, change `lastException:` to `LastException:`.
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

## Policies and AggregateException

In version 3.0, every entry point asks the policy about the exceptions that the operation actually threw. If the operation throws an `AggregateException`, the policy is asked about each inner exception, depth first, and the exception is retried only if the policy accepts all of them. An `AggregateException` with no inner exceptions goes to the policy itself. In version 2.x, only `RetryTask` and `ReliableTaskRunner` did this; `Retriable` gave the `AggregateException` itself to the policy, thus `RetryPolicies.Handle<HttpRequestException>()` refused an `AggregateException` that contained an `HttpRequestException`.

| Entry point | 2.x | 3.0 |
|-------------|-----|-----|
| `Retriable`, `ReliabilityPipeline` | The policy got the `AggregateException`. | The policy is asked about each inner exception. |
| The hedging policy, the circuit breaker policy, the fallback policy | Not in 2.x. | The policy is asked about each inner exception. |
| `RetryTask`, `ReliableTaskRunner` | The policy was asked about each inner exception. | No change. |

The strategy, the `onRetry` callback, `OnHedging`, `CircuitStateChange.LastException` and `FallbackArgs<T>.Exception` receive the `AggregateException` as thrown. A custom policy that examines `AggregateException` itself must now examine its inner exceptions instead.

## Asynchronous operations with Retry and StartNew

In version 3.0, the compiler gives the error CS0619 when you give an asynchronous operation to `Retriable.Retry` or to `RetryTask.Factory.StartNew`. In version 2.x, these calls compiled, but the retries did not operate:

| Call | 2.x | 3.0 |
|------|-----|-----|
| `Retriable.Retry(async () => ...)` | The call bound to `Retry<Task>`. It retried only an exception before the first `await`, and returned the task of the first attempt. | Compile error CS0619. |
| `Retriable.Retry(() => client.SendAsync(request))` | The same as the previous row. | Compile error CS0619. |
| `Retriable.Retry(() => stream.ReadAsync(buffer))`, or an operation that returns a different `ValueTask` or `ValueTask<TResult>` | The call bound to `Retry<ValueTask<int>>`. It retried only an exception that the method threw before it returned the `ValueTask`. | Compile error CS0619. |
| `RetryTask.Factory.StartNew(async () => ...)` | The lambda ran as `async void`. The task completed at the first `await`. An exception after it was not retried, and could stop the process. | Compile error CS0619. |
| `RetryTask.Factory.StartNew(() => DoAsync())`, where `DoAsync` returns a `ValueTask` or a `ValueTask<TResult>` | The task completed when `DoAsync` returned its `ValueTask`. A failure of the `ValueTask` was not retried. | Compile error CS0619. |
| `Retriable.RetryAsync(async () => client.SendAsync(request))`, with no `await` in the lambda | The call bound to `RetryAsync<Task<HttpResponseMessage>>`. It completed when the lambda returned the inner task, and did not retry a failure of that task. | Compile error CS0619. |
| `RetryTask<Task>.Factory.StartNew(...)`, or a different task type for `T` | The task completed when the function returned its task. A failure of that task was not retried. | `StartNew` throws a `NotSupportedException`, before it calls the function. The compiler cannot find this error. |
| `RetryTask<object>.Factory.StartNew(() => DoAsync())`, or an interface type for `T` | The task completed when the function returned its task. A failure of that task was not retried. | The task faults with a `NotSupportedException` when a try returns a `Task` or a `ValueTask`. The function is not retried. |

To correct the error, do these steps:

1. Replace `Retriable.Retry` with `Retriable.RetryAsync`. Use the same arguments.
2. Replace `RetryTask.Factory.StartNew(async () => ...)` and `RetryTask<Task>.Factory.StartNew(...)` with `Retriable.RetryAsync(async () => ...)`. If you used a `TaskCreationOptions` or a `TaskScheduler`, start the call with `Task.Factory.StartNew(...).Unwrap()`.
3. For an operation that returns a `ValueTask` or a `ValueTask<TResult>`, use `ReliabilityPipeline.ExecuteAsync`. As an alternative, call `AsTask()` in the operation, and use `Retriable.RetryAsync`.
4. In an `async` lambda for `RetryAsync`, use `await` on the task of the operation: `async () => await client.SendAsync(request)`.
5. Use `await` on the result.

A synchronous lambda that only throws an exception does not cause the error. It binds to the same overload as in version 2.x.

The `RetryAsync` error also occurs for a function that must return a task of a task, for example `() => Task.FromResult(someTask)`. Change the function to return the inner task, or put the inner task in a different type.

If an operation gives a `null` task to `Retriable.RetryAsync`, the attempt fails with an `InvalidOperationException`. In version 2.x, it failed with a `NullReferenceException`. The strategy and the policy decide if `RetryAsync` retries it, as for each other exception. If your policy examines `NullReferenceException`, change it.

## RetryTask

`RetryTask.Factory` and `RetryTask<T>.Factory` keep all their `StartNew` overloads.

- A retry does not block a thread. In version 2.x, the delay blocked a thread, and each retry ran synchronously on that thread. In version 3.0, a delay task and continuations start each retry on the scheduler of the caller.
- `RetryTask` has no retry callback, because the `Retrying` event is removed.
- If the scheduler refuses to queue a retry, the refusal is a failure of that try. The strategy and the policy decide if `RetryTask` retries it. In version 2.0, the task faulted immediately. If the scheduler refuses the first try, `StartNew` throws the `TaskSchedulerException`, as `Task.Factory.StartNew` does.
- The strategy receives a `RetryAttempt` with the attempt number, the previous delay, and the `AggregateException` of the try. The policy is asked about each exception that the try actually threw.
- If the operation stops, the task faults with the failures that the operation recorded. `RetryTask` keeps a maximum of 100 failures: the first 99 failures and the last failure. In version 2.x, it kept all the failures.
- The task that `RetryTask` returns obeys `TaskCreationOptions.RunContinuationsAsynchronously`. In version 2.x, a continuation of the task could run synchronously in the retry code.
- `RetryTask<T>.Factory.StartNew` throws a `NotSupportedException` when `T` is a task type. See [Asynchronous operations with Retry and StartNew](#asynchronous-operations-with-retry-and-startnew).

## ReliableTaskRunner

`ReliableTaskRunner` now implements `IAsyncDisposable`.

- Use `await using` to stop the runner at the end of a scope. `DisposeAsync` stops the operation, waits until the run ends, and releases the `CancellationTokenSource`. It does not throw the exception of the run. A second call has no effect.
- `StopAsync` cancels the token with `CancelAsync`. Thus, the callbacks of the token run on a thread-pool thread, not on the thread that calls `StopAsync`. In version 2.x, an exception from a callback came from `StopAsync` immediately. In version 3.0, `StopAsync` still waits until the run ends, and its task then faults with the exceptions of the callbacks, followed by the exceptions of the run if the run failed. `DisposeAsync` completes successfully.
- `StopAsync` returns a new task, not the task of the run. The task ends as the last run ended. If the run faulted with many exceptions, the task faults with all of them.
- After `DisposeAsync`, `StopAsync` does not cancel the token. Its task ends as the last run ended.
- If the policy throws an exception, the runner stops, and the task from `StopAsync` faults with that exception. In version 2.x, the runner stopped, and the exception was lost.
- The runner starts each restart on the thread pool (`TaskScheduler.Default`). In version 2.x, it used the scheduler of the caller of `Run`. If your run function must start on a special scheduler, start it on that scheduler in the run function.
- If the run function returns a `null` task, `Run` throws an `InvalidOperationException`. On a restart, a `null` task is a run that failed with an `InvalidOperationException`, and the policy decides if the runner restarts.
- `StopAsync` and `DisposeAsync` wait until a restart decision that is in progress ends. Then they use the outcome of the last run.
- Do not await the task from `StopAsync` in the run function. The task completes only when the run ends.

`Run(runFunction)` and `Run(runFunction, retryPolicy)` restart the operation immediately after each failure, with no limit, as in version 2.x. To wait between restarts, use the new overload `Run(runFunction, restartStrategy, retryPolicy)`:

```csharp
await using ReliableTaskRunner runner = ReliableTaskRunner.Run(
    cancellationToken => ProcessMessagesAsync(cancellationToken),
    new Exponential(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
    AnyExceptionPolicy.Default);
```

CAUTION: Use a strategy with no limit, for example `new Exponential(int.MaxValue, ...)`. A strategy with a limit stops the runner after that number of failed runs, and the operation does not run again.

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
