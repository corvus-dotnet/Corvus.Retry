# Corvus.Retry
[![Build Status](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml/badge.svg)](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml)
[![GitHub licence](https://img.shields.io/badge/License-Apache%202-blue.svg)](https://raw.githubusercontent.com/corvus-dotnet/Corvus.Retry/main/LICENSE)
[![IMM](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)

This library retries operations that fail, and restarts long-running operations that fail.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

If you use version 2.x, read the [migration guide from 2.x to 3.0](Documentation/Migrating-from-2.x-to-3.0.md) before you do the upgrade.

## Features

Two concepts control every retry:

- An `IRetryPolicy` decides **whether** an exception can cause a retry.
- An `IRetryStrategy` decides **when**: the number of attempts, and the delay before each retry.

The library gives these types:

### `Retriable`
`Retriable` puts retries around one synchronous or asynchronous call, with a small set of parameters.
### `RetryTask` and `RetryTaskFactory`
`RetryTaskFactory` is a task factory with retries. It follows .NET's own `TaskFactory`: make a configured factory, as you make `new TaskFactory(...)`, and keep it in a field. `Run` runs an asynchronous operation, as `Task.Run` does, and retries it. `StartNew` starts a synchronous function on a scheduler, as `Task.Factory.StartNew` does, and retries it. A configured factory adds timeouts, result retries, a fallback and a circuit breaker to the retries. `RetryTask.Run` and `RetryTask.Factory` use a factory with the default settings.
### `HedgedTask` and `HedgedTaskFactory`
`HedgedTaskFactory` starts more attempts in parallel when an attempt is slow or fails, and keeps the first acceptable outcome. Hedging is not a retry, thus it has its own types.
### `CircuitBreaker`
A `CircuitBreaker` stops calls to a service that fails frequently. Many factories can share one breaker.
### `Fallback<T>`
A `Fallback<T>` gives a value when the attempts of a factory fail.
### `RetryHandler`
`RetryHandler` is an `HttpMessageHandler` that applies a `RetryTaskFactory<HttpResponseMessage>` to each request of an `HttpClient`.
### `ReliableTaskRunner`
`ReliableTaskRunner` runs a long-running asynchronous operation. If the operation fails, the runner starts it again.

### Which entry point to use

| You want to | Use | Default without settings |
|-------------|-----|--------------------------|
| Retry one call, and give the strategy and the policy at the call | `Retriable.Retry` or `Retriable.RetryAsync` | 10 attempts, no delay, all exceptions |
| Retry one asynchronous call with the default settings | `RetryTask.Run` or `RetryTask<T>.Run` | 5 attempts, no delay, all exceptions |
| Use the same settings for many calls, or use timeouts, result retries, a fallback, a circuit breaker or an `OnRetry` callback | A configured `RetryTaskFactory` or `RetryTaskFactory<T>` in a field, and its `Run` method | 5 attempts, no delay, all exceptions |
| Start a synchronous function as a task on a scheduler, as `Task.Factory.StartNew` does | `StartNew` of a factory, or of `RetryTask.Factory` | 5 attempts, no delay, all exceptions |
| Start parallel attempts for a slow operation | A `HedgedTaskFactory` or `HedgedTask.Run` | 1 extra attempt after 2 seconds |
| Retry each request of an `HttpClient` | `RetryHandler` with a configured `RetryTaskFactory<HttpResponseMessage>` | Requests with a safe method only |
| Keep a long-running operation alive | `ReliableTaskRunner` | Restart after each failure |

`Retriable.RetryAsync(ct => …, cancellationToken)` and `RetryTask.Run(ct => …, cancellationToken)` have the same signature. They have different defaults: `Retriable` makes 10 attempts, and `RetryTask.Run` makes 5. Use `Retriable` when each call gives its own strategy and policy. Use `RetryTask.Run`, or a configured factory, when many calls use the same settings.

## Usage

### `Retriable`

`Retriable` puts retries around a method. If the method throws an exception, `Retriable` calls the method again.

```csharp
Retriable.Retry(() => DoSomethingThatMightFail());
```

The method can return a value:

```csharp
SomeResult result = Retriable.Retry(() => new SomeResult());
```

`Retriable` also has asynchronous overloads:

```csharp
Task resultTask = Retriable.RetryAsync(() => SomeOperationAsync());
```

Use `RetryAsync` for an asynchronous operation. The compiler gives the error CS0619 for these incorrect calls, because they cannot retry a failure of the task:

- `Retry` or `StartNew` with an operation that returns a `Task`, a `Task<T>`, a `ValueTask` or a `ValueTask<T>`. For a `ValueTask`, use `RetryTaskFactory.RunValueTask`, or call `AsTask()` in the operation.
- `RetryAsync` with an `async` lambda that returns a task without `await`, for example `async () => SomeOperationAsync()`. Write `async () => await SomeOperationAsync()`, or `() => SomeOperationAsync()`.

If the operation returns a `null` task, the attempt fails with an `InvalidOperationException`. The strategy and the policy decide if `RetryAsync` retries it.

Without a strategy and a policy, `Retriable` makes a maximum of 10 attempts with no delay (`RetryStrategies.Default`), and retries all exceptions (`AnyExceptionPolicy`).

The overloads for `Func<CancellationToken, Task<T>>` and `Func<CancellationToken, Task>` give a cancellation token to the operation. The overloads that have all the parameters also have an optional `onRetry` callback:

```csharp
string text = await Retriable.RetryAsync(
    ct => httpClient.GetStringAsync(uri, ct),
    cancellationToken,
    RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(1)),
    RetryPolicies.TransientHttp,
    continueOnCapturedContext: false,
    onRetry: args =>
    {
        Console.WriteLine($"Attempt {args.AttemptNumber} failed. Attempt {args.NextAttemptNumber} starts after {args.DelayBeforeRetry}.");
        return ValueTask.CompletedTask;
    });
```

The overloads that have a cancellation token obey these rules:

- `Retriable` examines the token before each attempt, also before the first attempt. If the token is cancelled, `Retriable` does not call the operation, and throws an `OperationCanceledException`.
- The token stops a delay immediately.
- `Retriable` does not retry an `OperationCanceledException` that the token caused.

When the strategy or the policy stops the retries, `Retriable` throws the exception of the last attempt.

`Retriable` awaits `onRetry` before each delay. The synchronous `Retry` overloads wait for the callback. They run it with no `SynchronizationContext`, and with `TaskScheduler.Default` as the current scheduler, thus a callback that awaits continues on the thread pool. An exception from the callback stops the call.

`Retriable` has no timeouts and no result retries. For these functions, use a configured `RetryTaskFactory`.

### `RetryTask.Run`

`RetryTask.Run` runs an asynchronous operation once with the default factory, `RetryTask.Factory`, and retries it when it throws an exception:

```csharp
string body = await RetryTask.Run(ct => httpClient.GetStringAsync(uri, ct), cancellationToken);

await RetryTask.Run(ct => SaveAsync(document, ct), cancellationToken);
```

The default factory makes a maximum of 5 attempts with no delay (`new Count()`), and retries all exceptions (`AnyExceptionPolicy`). It has no timeouts, no callback and no circuit breaker. The operation receives the caller's token. `RetryTask<T>.Run` does the same with `RetryTask<T>.Factory`.

To change any setting, make a configured factory.

### `RetryTaskFactory`

Make one `RetryTaskFactory` and keep it in a field. A factory is immutable, and many threads can use it at the same time. Each call has its own state.

```csharp
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

RetryTaskFactory factory = new(
    RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    RetryPolicies.TransientHttp)
{
    AttemptTimeout = TimeSpan.FromSeconds(5),
};

string body = await factory.Run(ct => httpClient.GetStringAsync(uri, ct), cancellationToken);
```

The operation receives a cancellation token. The factory cancels this token when the caller cancels, or when a timeout expires. Give the token to all the asynchronous methods that the operation calls.

`RetryTaskFactory<T>` is the factory for operations that return a `T`. It can also retry a result and use a fallback.

#### Settings

A factory has three constructors:

- `new RetryTaskFactory()` uses the default strategy and policy.
- `new RetryTaskFactory(strategy, policy)` sets the strategy and the policy.
- `new RetryTaskFactory(other)` copies all the settings of `other`. Use it with an object initialiser to make a changed copy: `new RetryTaskFactory(factory) { TotalTimeout = TimeSpan.FromSeconds(30) }`. The copy shares the strategy, the policy, the callback and the circuit breaker of `other`. A factory is a class, not a record, thus this constructor replaces the `with` expression.

Set the other settings with an object initialiser. All the properties are `init`-only.

| Property | Default | Description |
|----------|---------|-------------|
| `Strategy` | `new Count()`: 5 attempts, no delay | The `IRetryStrategy` that gives the delay before each retry, or stops the retries. |
| `Policy` | `AnyExceptionPolicy.Default` | The `IRetryPolicy` that accepts or refuses each exception. It applies only to exceptions. |
| `AttemptTimeout` | `null` (no limit) | The time limit for each attempt. |
| `TotalTimeout` | `null` (no limit) | The time limit for all the attempts and all the delays. |
| `OnRetry` | `null` | A callback that the factory awaits before each delay. |
| `CircuitBreaker` | `null` | A shared `CircuitBreaker`. See [Circuit breaker](#circuit-breaker). |
| `TimeProvider` | `TimeProvider.System` | The source of the time for the delays and the timeouts. |
| `ContinueOnCapturedContext` | `false` | If `true`, `Run` and `RunValueTask` continue on the captured context after each `await`. |

`RetryTaskFactory<T>` adds these properties:

| Property | Default | Description |
|----------|---------|-------------|
| `CanRetryResult` | `null` | A predicate that returns `true` for a result that is a failure. |
| `Fallback` | `null` | The `Fallback<T>`. See [Fallback](#fallback). |

Each `init` accessor examines its value, and throws an exception for an incorrect value. The `ParamName` of the exception is the name of the property:

- `ArgumentOutOfRangeException`: a timeout is zero, negative, or more than approximately 49.7 days. For no limit, use `null`, not `Timeout.InfiniteTimeSpan`.
- `ArgumentNullException`: `Strategy`, `Policy` or `TimeProvider` is `null`.

The constructors throw an `ArgumentNullException` for a `null` argument. Its `ParamName` is `strategy`, `policy` or `other`.

To make one attempt and no retry, set `Strategy` to `new Count(1)`, `new DoNotRetry()`, or a `RetryStrategies` method with `retries: 0`.

#### `Run`, `RunValueTask` and `StartNew`

A factory has three kinds of method:

| Method | Operation | Settings that it uses | When the retries stop |
|--------|-----------|-----------------------|-----------------------|
| `Run` | `Func<CancellationToken, Task>` or `Func<CancellationToken, Task<T>>` | All | It throws the exception of the last attempt, with its original stack trace. |
| `RunValueTask` | `Func<CancellationToken, ValueTask>` or `Func<CancellationToken, ValueTask<T>>` | All | It throws the exception of the last attempt, with its original stack trace. |
| `StartNew` | A synchronous `Action` (`RetryTaskFactory`) or `Func<T>` (`RetryTaskFactory<T>`), as `Task.Factory.StartNew` takes | `Strategy`, `Policy` and `TimeProvider` only | The task faults with an `AggregateException` of the failures. See [StartNew](#startnew). |

Use `Run` for a method that returns a `Task` or a `Task<T>`. Use `RunValueTask` for a method that returns a `ValueTask` or a `ValueTask<T>`:

```csharp
int bytesRead = await factory.RunValueTask(ct => stream.ReadAsync(buffer, ct), cancellationToken);
```

The two methods have different names. With one name, the compiler cannot select an overload for an `async` lambda (error CS0121). When there is no timeout and no callback, and every attempt completes synchronously, `RunValueTask` allocates no memory.

`Run` has the name of `Task.Run`, but it operates differently:

- `Run` does not queue the operation to the thread pool. The first attempt runs on the caller's thread until its first `await`.
- `Run` gives a cancellation token to the operation.
- `Run` has no overload for a synchronous function. For a synchronous function, use `Retriable.Retry` or `StartNew`.
- `ReliableTaskRunner.Run` is a different method. It restarts a long-running operation. See [ReliableTaskRunner](#reliabletaskrunner).

These rules also apply to `Run` and `RunValueTask`:

- The compiler gives the error CS0619 for an `async` lambda that returns a task without `await`, for example `Run(async ct => GetAsync(ct))`. Write `Run(async ct => await GetAsync(ct))`, or `Run(ct => GetAsync(ct))`.
- If the operation returns a `null` task, the attempt fails with an `InvalidOperationException`. The strategy and the policy decide if the factory retries it.
- `RetryTaskFactory<T>` cannot retry a function that returns a task. When `T` is `Task`, `Task<TResult>`, `ValueTask` or `ValueTask<TResult>`, `Run`, `RunValueTask` and `StartNew` throw a `NotSupportedException`, and do not call the function. Use the result type of the task as `T`.

#### Timeouts and cancellation

A configured factory has two time limits:

- **Attempt timeout.** When `AttemptTimeout` expires, the factory cancels the token of the attempt. The attempt then fails with a `RetryTimeoutException`. The strategy and the policy decide if the factory retries it.
- **Total timeout.** When `TotalTimeout` expires, the factory cancels the token of the attempt, or stops the delay. No other attempt starts, and the call throws a `RetryTimeoutException`.

`RetryTimeoutException` derives from `TimeoutException`. Its `Timeout` property gives the time limit that expired. When the total timeout expires between two attempts, the inner exception is the exception of the last attempt.

The two timeouts are cooperative. The factory cancels the token, and then waits until the operation stops.

When the caller cancels the token, the call throws an `OperationCanceledException` that carries the caller's token. The factory does not retry this exception, and does not give it to the policy. The token also stops a delay immediately.

If the operation throws an `AggregateException`, the factory asks the policy about each inner exception, depth first, and retries only if the policy accepts all of them. An `AggregateException` with no inner exceptions goes to the policy itself. The strategy and `OnRetry` receive the `AggregateException` as thrown.

```csharp
RetryTaskFactory factory = new(
    RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromSeconds(1)),
    RetryPolicies.TransientHttp)
{
    AttemptTimeout = TimeSpan.FromSeconds(10),
    TotalTimeout = TimeSpan.FromSeconds(30),
};

using CancellationTokenSource cancellation = new();
try
{
    string body = await factory.Run(ct => httpClient.GetStringAsync(uri, ct), cancellation.Token);
}
catch (RetryTimeoutException ex)
{
    Console.WriteLine($"The time limit of {ex.Timeout} expired.");
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("The caller cancelled the call.");
}
```

> **Note:** A policy for one exception type does not retry attempt timeouts. An attempt timeout gives a `RetryTimeoutException`. To retry attempt timeouts, the policy must also accept `RetryTimeoutException` or `TimeoutException`, for example `RetryPolicies.For<HttpRequestException>().Or<TimeoutException>()`. `RetryPolicies.TransientHttp` and `AnyExceptionPolicy` accept `RetryTimeoutException`.

#### The retry callback

The factory awaits `OnRetry` before each delay. The callback receives a `RetryEventArgs`:

- `AttemptNumber` is the number of the attempt that failed. The first attempt is 1.
- `NextAttemptNumber` is the number of the attempt that starts after the delay.
- `LastException` is the exception of the attempt. It is `null` when a result caused the retry.
- `Result` is the result that caused the retry, or `null`.
- `DelayBeforeRetry` is the delay before the next attempt.

The factory does not call `OnRetry` for the last failure. An exception from the callback stops the call.

```csharp
RetryTaskFactory factory = new(
    RetryStrategies.Linear(retries: 3, delay: TimeSpan.FromSeconds(1)),
    AnyExceptionPolicy.Default)
{
    OnRetry = args =>
    {
        Console.WriteLine($"Attempt {args.AttemptNumber} failed. Attempt {args.NextAttemptNumber} starts after {args.DelayBeforeRetry}.");
        return ValueTask.CompletedTask;
    },
};
```

#### Retry a result

`RetryTaskFactory<T>` can also retry a result. If `CanRetryResult` returns `true`, the factory uses the result as a failure. The strategy, the callback and the delay then apply. The policy applies only to exceptions.

- Before the next attempt, the factory disposes a rejected result that implements `IDisposable`. It does this after the callback. An exception from that disposal stops the call: the caller gets that exception, and the fallback does not apply.
- If the call stops with an exception after the operation returned a result, the factory also disposes that result. For example, the caller cancels, the total timeout expires, or `CanRetryResult`, the strategy or `OnRetry` throws an exception.
- If no retries remain, the factory returns the last result. It does not throw an exception, and it does not dispose the result.

`RetryStrategies.RetryAfter` uses the `Retry-After` header of an `HttpResponseMessage` result for the delay:

```csharp
RetryTaskFactory<HttpResponseMessage> factory = new(
    RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(1))),
    RetryPolicies.TransientHttp)
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(10),
};

using HttpResponseMessage response = await factory.Run(ct => httpClient.GetAsync(uri, ct), cancellationToken);
```

#### Order of the parts

`Run` and `RunValueTask` apply the parts of a factory in this sequence, from the outermost part to the innermost part:

```text
Run(operation, cancellationToken)
 └─ 1. Fallback                    Fallback (RetryTaskFactory<T> only)
     └─ 2. Total timeout           TotalTimeout
         └─ 3. Retry               Strategy, Policy, OnRetry, CanRetryResult
             └─ 4. Circuit breaker CircuitBreaker
                 └─ 5. Attempt timeout  AttemptTimeout
                     └─ 6. The operation
```

A `HedgedTaskFactory` uses the same sequence, with hedging in place of the retry part.

Each part is optional. If a setting is `null`, the factory does not use that part, and does not allocate memory for it. These are the results of the sequence:

- The fallback receives the outcome of the attempts. This outcome can be a `RetryTimeoutException` or a `CircuitRejectedException`.
- The total timeout includes all the attempts and all the delays.
- The circuit breaker counts each attempt as one call. It counts an attempt timeout as a failure.

#### Fallback

A fallback gives a value when the attempts fail. It applies only to `RetryTaskFactory<T>` and `HedgedTaskFactory<T>`.

The constructor of `Fallback<T>` takes the action that gives the fallback value. Set the other properties with an object initialiser:

| `Fallback<T>` member | Default | Description |
|----------------------|---------|-------------|
| `Action` (constructor parameter) | None (required) | The function that gives the fallback value. |
| `Policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that get the fallback value. |
| `AppliesToResult` | `null` | A predicate that returns `true` for a result that gets the fallback value. |
| `OnFallback` | `null` | A callback that the factory awaits before it calls `Action`. |

The property of the factory and its type have the same name, `Fallback`. In an object initialiser, use a target-typed `new`:

```csharp
RetryTaskFactory<string> factory = new(
    RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(500)),
    AnyExceptionPolicy.Default)
{
    TotalTimeout = TimeSpan.FromSeconds(5),
    Fallback = new(args => ValueTask.FromResult("cached value"))
    {
        OnFallback = args =>
        {
            Console.WriteLine($"Fallback after {args.Exception?.GetType().Name}.");
            return ValueTask.CompletedTask;
        },
    },
};
```

- If the attempts end with an exception that `Policy` accepts, the factory calls `OnFallback` and then `Action`. It returns the value of `Action`.
- If `AppliesToResult` returns `true` for a result, the factory calls `OnFallback` with that result, then disposes the result if it implements `IDisposable`, and then calls `Action`. Thus `OnFallback` can examine the result, for example to log the body of a response, and `Action` receives a result that is already disposed. This is the same order as the retry part, which disposes a rejected result after `OnRetry`.
- The fallback does not apply when the caller cancels the token.
- `FallbackEventArgs<T>` gives the `Exception` or the `Result`, and the `CancellationToken` of the caller. The total timeout does not apply to the fallback.
- An exception from `OnFallback` or from `Action` stops the call. The factory does not catch it. If `OnFallback` throws for a rejected result, the factory disposes the result.
- The fallback applies only to the outcome of the attempts: the exception or the result of the last attempt, a `RetryTimeoutException`, or a rejection by the circuit breaker. An exception from `CanRetryResult`, `OnRetry`, the strategy, a policy, `OnStateChanged`, `OnHedgedAttempt`, `ResultIsFailure` or `OperationForAttempt` stops the call. The fallback does not give a value for it.

#### Circuit breaker

A circuit breaker stops calls to a service that fails frequently. One `CircuitBreaker` instance holds the state. Many factories and threads can use the same instance.

The settings are optional constructor parameters, not properties, because the breaker reads the clock of its `TimeProvider` when you construct it. Use named arguments:

| `CircuitBreaker` parameter | Default | Description |
|----------------------------|---------|-------------|
| `failureRate` | `0.1` | The ratio of failures to calls at which the circuit opens. The value must be more than 0, and 1 or less. |
| `minimumCalls` | `100` | The minimum number of calls in the window before the circuit can open. The minimum value is 2. |
| `samplingWindow` | 30 seconds | The length of the sliding window that counts the calls and the failures. The minimum value is 500 ms. |
| `openDuration` | 5 seconds | The time for which the circuit stays open. The minimum value is 500 ms. |
| `policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that are failures. |
| `onStateChanged` | `null` | A callback for each change of state. |
| `timeProvider` | `TimeProvider.System` | The source of the time for the window and the open duration. |

The breaker has a read-only property for each parameter: `FailureRate`, `MinimumCalls`, `SamplingWindow`, `OpenDuration`, `Policy`, `OnStateChanged` and `TimeProvider`. An incorrect value gives an `ArgumentOutOfRangeException`, with the name of the parameter as its `ParamName`. For `policy` and `timeProvider`, `null` gives the default.

```csharp
CircuitBreaker breaker = new(
    failureRate: 0.5,
    minimumCalls: 10,
    openDuration: TimeSpan.FromSeconds(15),
    onStateChanged: change =>
    {
        Console.WriteLine($"The circuit changed from {change.From} to {change.To}.");
        return ValueTask.CompletedTask;
    });

RetryTaskFactory factory = new(
    RetryStrategies.RetryAfter(RetryStrategies.Constant(retries: 3, delay: TimeSpan.FromSeconds(1))),
    RetryPolicies.TransientHttp.Or(RetryPolicies.For<CircuitRejectedException>()))
{
    CircuitBreaker = breaker,
    AttemptTimeout = TimeSpan.FromSeconds(5),
};
```

The circuit has four states (`CircuitState`):

- **Closed.** The breaker counts the calls and the failures in a sliding window of `samplingWindow`, with 10 buckets. If the window has `minimumCalls` calls or more, and the ratio of failures is `failureRate` or more, the circuit opens.
- **Open.** The breaker rejects each call immediately with a `CircuitRejectedException`. After `openDuration`, the circuit goes to half-open.
- **HalfOpen.** The breaker lets one test call through, and rejects the other calls with a `CircuitRejectedException`. If the test call succeeds, the circuit closes and the counts start again. If the test call fails, the circuit opens again.
- **Isolated.** `Isolate()` sets this state. The breaker rejects each call with a `CircuitIsolatedException` until you call `Close()`. `Close()` closes the circuit and clears the counts.

`CircuitIsolatedException` derives from `CircuitRejectedException`. Thus, a `catch (CircuitRejectedException)` block also catches a rejection by an isolated circuit.

These rules apply to the breaker:

- A failure is an exception that the `Policy` of the breaker accepts, or a result that `CanRetryResult` (or `ResultIsFailure` of a hedged factory) rejects. For an `AggregateException`, the breaker asks the `Policy` about each inner exception, and the call is a failure only if the policy accepts all of them. `CircuitStateChange.LastException` is the exception as thrown.
- An attempt timeout is always a failure. The breaker does not ask its `Policy` about a `RetryTimeoutException`. Thus, a `Policy` such as `RetryPolicies.For<HttpRequestException>()` does not make the breaker ignore timeouts.
- If the `Policy` of the breaker throws an exception, the call throws that exception. The breaker does not count the call. If the call was the test call of a half-open circuit, the next call can be the test call.
- A cancellation by the caller is not a call and not a failure. A call that the breaker rejects is not a call.
- A test call that started before `Isolate()`, `Close()` or a subsequent opening does not change the state of the circuit. A test call that ends after `Close()` is not counted in the new window.
- The circuit goes from `Open` to `HalfOpen` on the first call after the open duration, not at the end of the open duration. Until that call, `State` gives `Open`.
- The breaker measures time with the monotonic timestamp of its `TimeProvider`. A change to the system clock does not change the window or the open duration.
- The breaker calls `OnStateChanged` after it releases its lock. A factory awaits the callback. `Isolate()` and `Close()` wait until the callback completes, thus the callback must be fast. They run the callback with no `SynchronizationContext`, and with `TaskScheduler.Default` as the current scheduler. Thus, a callback that awaits on a UI thread, or on a scheduler that runs one task at a time, does not cause a deadlock. An exception from the callback stops the call.

The breaker is inside the retry part. Thus, a rejection is a failure of one attempt, and the strategy and the policy decide if the factory retries it:

- `AnyExceptionPolicy` retries a `CircuitRejectedException`. `RetryPolicies.TransientHttp` does not retry it.
- `CircuitRejectedException.RetryAfter` gives the time until the open duration ends. It is `null` when the circuit is half-open or isolated.
- `RetryStrategies.RetryAfter` uses `CircuitRejectedException.RetryAfter` as the delay. Thus, the next attempt starts when the open duration ends.

### `HedgedTaskFactory`

Hedging starts more attempts in parallel when an attempt is slow or fails. The first acceptable outcome wins.

The constructor takes the two settings that define the hedging. Set the other settings with an object initialiser:

```csharp
HedgedTaskFactory<string> hedged = new(extraAttempts: 2, stagger: TimeSpan.FromMilliseconds(300))
{
    AttemptTimeout = TimeSpan.FromSeconds(5),
    OperationForAttempt = attemptNumber => attemptNumber == 1
        ? null
        : ct => httpClient.GetStringAsync(secondaryUri, ct),
};

string body = await hedged.Run(ct => httpClient.GetStringAsync(uri, ct), cancellationToken);
```

| Constructor parameter or property | Default | Description |
|-----------------------------------|---------|-------------|
| `extraAttempts` | `1` | The maximum number of attempts after the first attempt. The range is 1 thru 10. The total number of attempts is `extraAttempts` + 1. The `ExtraAttempts` property gives the value. |
| `stagger` | `null`: 2 seconds | The time after the start of an attempt at which the next attempt starts. `TimeSpan.Zero` starts all the attempts at the same time. `Timeout.InfiniteTimeSpan` starts the next attempt only after a failure. The `Stagger` property gives the value. |
| `Policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that are failures. |
| `OnHedgedAttempt` | `null` | A callback that the factory awaits before it starts each attempt after the first. It receives a `HedgedAttemptEventArgs`. |
| `AttemptTimeout`, `TotalTimeout`, `CircuitBreaker`, `TimeProvider`, `ContinueOnCapturedContext` | As for `RetryTaskFactory` | As for `RetryTaskFactory`. |

`HedgedTaskFactory<T>` adds these properties:

| Property | Default | Description |
|----------|---------|-------------|
| `ResultIsFailure` | `null` | A predicate that returns `true` for a result that is a failure. Hedging does not retry, thus this property is not named `CanRetryResult`. |
| `Fallback` | `null` | The `Fallback<T>`. See [Fallback](#fallback). |
| `OperationForAttempt` | `null` | A function that gives a different operation for each attempt. |

The factory awaits `OnHedgedAttempt` before it starts each attempt after the first. The callback receives a `HedgedAttemptEventArgs`:

- `AttemptNumber` is the number of the attempt that is about to start. The first hedged attempt is 2.
- `LastException` is the exception of the attempt that failed and caused this attempt. It is `null` when the stagger expired, or when a result that `ResultIsFailure` rejected caused the attempt.

```csharp
HedgedTaskFactory hedged = new(extraAttempts: 2, stagger: TimeSpan.FromMilliseconds(300))
{
    OnHedgedAttempt = args =>
    {
        Console.WriteLine(args.LastException is null
            ? $"Attempt {args.AttemptNumber} starts because the stagger expired."
            : $"Attempt {args.AttemptNumber} starts because attempt {args.AttemptNumber - 1} failed with {args.LastException.GetType().Name}.");
        return ValueTask.CompletedTask;
    },
};
```

An exception from the callback stops the call.

A hedged factory differs from a `RetryTaskFactory` in these ways:

- It has no strategy, no `OnRetry` and no retry policy. Its `Policy` decides which exceptions are failures.
- It has only `Run`. It has no `RunValueTask` and no `StartNew`. For an operation that returns a `ValueTask`, call `AsTask()` in the operation: `ct => GetAsync(ct).AsTask()`.
- It has no copy constructor.

`HedgedTask.Run` and `HedgedTask<T>.Run` use a hedged factory with the default settings, `HedgedTask.Factory` and `HedgedTask<T>.Factory`.

These rules apply to hedging:

- The factory starts attempt 1. It starts the next attempt when `Stagger` expires, or immediately when an attempt fails.
- Attempt 1 runs on the thread of the caller until its first `await`. Attempts 2 and higher start on the thread pool, with no `SynchronizationContext`. Thus, an attempt that blocks its thread before its first `await` does not delay the next attempt, the cancellation or the total timeout. An attempt 1 that blocks also blocks the caller.
- A failure is an exception that `Policy` accepts, or a result for which `ResultIsFailure` returns `true`. An exception that `Policy` refuses is not a failure: the factory throws it immediately. For an `AggregateException`, the policy is asked about each inner exception; `OnHedgedAttempt` and the caller receive the aggregate as thrown.
- The first outcome that is not a failure wins. The factory returns it immediately, and cancels the token of each other attempt. It does not wait for the cancelled attempts, and it disposes their results if they implement `IDisposable`.
- The factory does not start an attempt after it received an outcome that wins. With a `stagger` of zero, an attempt 1 that wins synchronously stops all the other attempts. An attempt 2 or higher completes on the thread pool, thus the factory can start more attempts before it receives that outcome.
- `OnHedgedAttempt` can run for an attempt that then does not start, because an outcome won during the callback. With a `stagger` of zero, if attempt 1 fails synchronously, `OnHedgedAttempt` for attempt 2 receives that failure in `LastException`.
- If all the attempts fail, the factory throws the last exception, or returns the last result. When the total timeout expires, the `RetryTimeoutException` has the exception of the last attempt that failed as its inner exception, or `null` if the last outcome was a rejected result or no attempt has failed.
- `OperationForAttempt` receives the attempt number, from 1. It can give a different operation for each attempt, for example a different endpoint. If it returns `null`, the attempt uses the operation that you give to `Run`.
- Each attempt goes through the circuit breaker, and has its own attempt timeout. The total timeout and the fallback apply to all the attempts. A call that the caller cancels, or that `OnHedgedAttempt` or a refused exception stops, releases the half-open test call before `Run` throws, thus a caller who retries at once makes the next test call. When the total timeout stops the call, each attempt still in flight is a failed call for the breaker.
- When the caller cancels or the total timeout expires, the hedged call stops immediately, also if an attempt ignores its token. (A `RetryTaskFactory` waits until the attempt stops.) A cancellation by the caller gives an `OperationCanceledException` that carries the caller's token.

The operation must be idempotent. It must also be safe when two or more attempts run at the same time.

#### Hedging and retries together

One factory cannot hedge and retry. To retry a hedged call, use two factories. Run the hedged factory in the operation of the retry factory. Give a circuit breaker to the inner factory only: if both factories share one breaker, the outer attempt takes the single test call of a half-open circuit, the inner attempt is then rejected, and the circuit never closes. See `Examples/composed-factories.cs`:

```csharp
string body = await retries.Run(
    ct => hedged.Run(c => httpClient.GetStringAsync(uri, c), ct),
    cancellationToken);
```

### `RetryHandler`

`RetryHandler` is a `DelegatingHandler` in the namespace `Corvus.Retry.Http`. It applies a `RetryTaskFactory<HttpResponseMessage>` to each request. Thus, callers use the `HttpClient` directly, and do not put each call in `Run`. Every setting of the factory applies: the retries, the result retries, the timeouts, the circuit breaker, the fallback, `OnRetry`, and the disposal of each rejected response. `RetryStrategies.RetryAfter` uses the `Retry-After` header. `RetryHandler` is in the `Corvus.Retry` package, and adds no dependency.

```csharp
using Corvus.Retry.Http;

RetryTaskFactory<HttpResponseMessage> retries = new(
    RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(1))),
    RetryPolicies.TransientHttp)
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(10),
    TotalTimeout = TimeSpan.FromSeconds(60),
};

HttpClient client = new(new RetryHandler(retries, new SocketsHttpHandler()));
```

The constructors are `RetryHandler(factory)` and `RetryHandler(factory, innerHandler)`. The `Factory` property gives the factory.

With `IHttpClientFactory`, use `AddRetryHandler` from the package `Corvus.Retry.Extensions.Http`. This package depends on `Microsoft.Extensions.Http`:

```csharp
services.AddHttpClient<CatalogueClient>().AddRetryHandler(retries);

// Also retry a POST request with content that can be sent again.
services.AddHttpClient<OrdersClient>().AddRetryHandler(retries, RetryHandler.CanReplayContent);
```

Make the factory one time. `IHttpClientFactory` makes a new handler chain from time to time. Each chain gets a new `RetryHandler`, and all of them share the factory and its circuit breaker. Add the retry handler after the handlers that must run one time for each call, and before the handlers that must run one time for each attempt.

These rules apply to the handler:

- `CanRetryRequest` decides if the handler can send a request more than one time. Its default is `RetryHandler.IsSafeToRetry`: the method is GET, HEAD, PUT, DELETE, OPTIONS or TRACE, and `RetryHandler.CanReplayContent` returns `true`.
- `RetryHandler.CanReplayContent` returns `true` for no content, `ByteArrayContent` and the types that derive from it (for example `StringContent` and `FormUrlEncodedContent`), `ReadOnlyMemoryContent` and `JsonContent`. It returns `false` for `StreamContent`, `MultipartContent` and all other content types.
- The handler sends the same `HttpRequestMessage` for each attempt. A `StreamContent` that cannot seek cannot be sent again: the second attempt fails with an `InvalidOperationException`. Thus, the default refuses it.
- To retry a POST request, set `CanRetryRequest = RetryHandler.CanReplayContent`. Do this only when the server can receive the same request two times without harm.
- A request that `CanRetryRequest` refuses gets one attempt. The timeouts, the circuit breaker, `CanRetryResult` and the fallback of the factory still apply. A rejected response is not retried: the handler returns it, not disposed, or the fallback replaces it. The breaker counts it. The handler does not call `OnRetry`.
- The synchronous `HttpClient.Send` throws a `NotSupportedException`. Use `SendAsync`, or a method that calls it, such as `GetAsync`.
- The handler does not hedge. Parallel attempts cannot share one `HttpRequestMessage`. To hedge, put the call in `Run` of a `HedgedTaskFactory<T>`.

The exceptions from a call through the handler are these:

- When an attempt timeout or the total timeout of the factory expires, the call throws a `RetryTimeoutException`, not a `TaskCanceledException`.
- When the caller cancels, the call throws a `TaskCanceledException` that carries the caller's token. The handler does not retry it.
- When the circuit is open, the call throws a `CircuitRejectedException`. Its `RetryAfter` gives the time until the open duration ends. `RetryPolicies.TransientHttp` does not retry it.

The handler has these limits:

- The handler returns the response when the headers arrive. `HttpClient` then reads a buffered body. Thus, a failure while the body downloads is not retried, and `AttemptTimeout` does not include the download. With `HttpCompletionOption.ResponseHeadersRead`, a later read of the body also fails outside the handler. If that matters, put the whole call in `Run` of a `RetryTaskFactory<string>` instead, for example `factory.Run(ct => client.GetStringAsync(uri, ct), cancellationToken)`. `Examples/http-client-resilience.cs` shows this.
- `HttpClient.Timeout` (100 seconds by default) is outside the handler. It limits all the attempts and all the delays together, and it can stop a retry delay. When it expires, the call throws a `TaskCanceledException` with an inner `TimeoutException`. The handler sees this as a cancellation by its caller. Thus, the fallback does not apply to it.
- Make the `TotalTimeout` of the factory shorter than `HttpClient.Timeout`.

`Examples/http-handler.cs` shows the handler.

### `StartNew`

`RetryTask.Factory`, `RetryTask<T>.Factory` and each configured factory have the `StartNew` overloads of `TaskFactory`, with retries. Call `StartNew()` with the same parameters that you give to `Task.Factory.StartNew()`. Some overloads also take an `IRetryStrategy`, an `IRetryPolicy`, or both. An overload without a strategy or a policy uses the `Strategy` or the `Policy` of the factory.

```csharp
Task<string> task = RetryTask<string>.Factory.StartNew(() => File.ReadAllText("settings.json"), new Count(5), AnyExceptionPolicy.Default);

RetryTaskFactory<string> files = new(new Count(5), RetryPolicies.For<IOException>());
Task<string> settings = files.StartNew(() => File.ReadAllText("settings.json"));
```

The default strategy is `new Count()` (5 attempts) and the default policy is `AnyExceptionPolicy`. These defaults are different from the defaults of `Retriable`.

- `StartNew` uses only the `Strategy`, the `Policy` and the `TimeProvider` of the factory. If the factory has an `AttemptTimeout`, a `TotalTimeout`, an `OnRetry`, a `CircuitBreaker`, a `CanRetryResult` or a `Fallback`, each `StartNew` overload throws an `InvalidOperationException`, and does not call the function. The message names the settings. Use `Run`, or a factory without these settings.
- `ContinueOnCapturedContext` does not apply to `StartNew`. `StartNew` queues each try to its scheduler, and makes its retry decisions on the thread pool.
- A retry does not block a thread. A delay task and continuations start each retry on the scheduler of the caller. The delays use the `TimeProvider` of the factory.
- If the operation stops, the task faults with the failures that the operation recorded, inside one `AggregateException`. Thus `task.Exception.InnerException` is that `AggregateException`, and its `InnerExceptions` are the failures of the tries. `StartNew` keeps a maximum of 100 failures: the first 99 failures and the last failure. `Run`, by contrast, throws the exception of the last attempt.
- A cancellation gives a `Canceled` task.
- The returned task obeys `TaskCreationOptions.RunContinuationsAsynchronously`.
- `StartNew` has no retry callback.
- `StartNew` retries only synchronous operations. `RetryTask<T>.Factory.StartNew` throws a `NotSupportedException` when `T` is a `Task`, a `Task<TResult>`, a `ValueTask` or a `ValueTask<TResult>`. When `T` is `object` or an interface, `StartNew` examines the result of each try: if the function returned a `Task` or a `ValueTask`, the task faults with `NotSupportedException`. For an asynchronous operation, use `Run`.
- If the scheduler refuses the first try, `StartNew` throws the `TaskSchedulerException`. If it refuses a retry, the refusal is a failure of that try, and the strategy and the policy decide.

The overloads that take a state object use the same nullable annotations as `Task.Factory.StartNew()`: the state is `object?`, and the action or function takes an `object?` argument. Version 1 used non-nullable `object`. If your project uses nullable reference types, change each method or delegate that takes `object`. It must take `object?`, or examine the state for `null` before it uses the state:

```csharp
RetryTask.Factory.StartNew(state => Process((string)state!), "input", new Count(5), new AnyExceptionPolicy());
```

### Policy and Strategy

Two types control the retries:

- An `IRetryPolicy` decides if an exception is permitted to cause a retry.
- An `IRetryStrategy` decides the number of attempts and the delay before each retry.

#### `IRetryPolicy`

A policy examines the exception that the operation threw. It returns `true` if the library can retry the operation. Thus, a policy can retry transient exceptions (for example, a network timeout) and stop at other exceptions (for example, incorrect input).

No policy makes the library retry an `OperationCanceledException` that the token of the caller caused. The library does not give this exception to the policy.

There are three built-in policy classes:

##### `AnyExceptionPolicy`
This policy retries all exceptions. It is the default policy. `AnyExceptionPolicy.Default` is a shared instance.

##### `DoNotRetryPolicy`
This policy does not retry an exception. Use it to stop the retries without a change to your retry code. For example:

```csharp
IRetryPolicy policy = isDebuggingOrWhatever ? new DoNotRetryPolicy() : AnyExceptionPolicy.Default;

var result = Retriable.Retry(() => DoSomething(), CancellationToken.None, new Count(10), policy);
```
##### `AggregatePolicy`
This policy retries an exception only if all its child policies accept the exception.

```csharp
var aggregatePolicy =
  new AggregatePolicy
  {
    Policies =
    {
      new CustomPolicy1(),
      new CustomPolicy2(),
    },
  };
```

An `AggregatePolicy` with no child policies retries all exceptions, because no child refuses the retry. `RetryPolicies.All` gives the same result, and cannot change after you make it.

##### Policy combinators

The static class `RetryPolicies` makes immutable policies. Many threads can use one instance at the same time.

| Member | The policy accepts |
|--------|--------------------|
| `RetryPolicies.FromPredicate(predicate)` | An exception for which the predicate returns `true` |
| `RetryPolicies.For<TException>(predicate)` | An exception of type `TException` (or a derived type). The predicate is optional. Add more types with `.Or<T>()`. |
| `RetryPolicies.ForInner<TException>(predicate)` | An exception of type `TException` in the exception, its `InnerException` chain, or the inner exceptions of an `AggregateException`, depth first |
| `RetryPolicies.Not(policy)` | An exception that `policy` refuses |
| `RetryPolicies.Any(policies)` | An exception that one or more of the policies accept |
| `RetryPolicies.All(policies)` | An exception that all the policies accept |
| `RetryPolicies.TransientHttp` | An `HttpRequestException` with no status code, or with status 408, 429, or 500 or more. Also a `RetryTimeoutException`. |

The C# 14 extension members `policy.Or(other)`, `policy.And(other)` and `policy.Not()` combine the policies that you have.

```csharp
IRetryPolicy policy = RetryPolicies.For<HttpRequestException>(ex => ex.StatusCode != HttpStatusCode.NotFound)
    .Or<RetryTimeoutException>()
    .Or(RetryPolicies.ForInner<SocketException>())
    .And(RetryPolicies.For<ArgumentException>().Not());
```

`RetryPolicies.TransientHttp` refuses a `TaskCanceledException`, a `TimeoutException` that is not a `RetryTimeoutException`, and a `CircuitRejectedException`.

`RetryPolicies.IsTransientHttpResponse(response)` returns `true` for a status of 408, 429, or 500 or more. Use it as `CanRetryResult` in a `RetryTaskFactory<HttpResponseMessage>`.

##### Custom policy
To make a custom policy, implement the `IRetryPolicy` interface and its `bool CanRetry(Exception exception)` method.

For example, an HTTP service sometimes sends the status `429 - Too Many Requests`. This policy retries only that failure:

```csharp
public class RetryOnTooManyRequestsPolicy : IRetryPolicy
{
  public bool CanRetry(Exception exception)
  {
    return exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
  }
}
```

A policy can have calls from many threads at the same time. Do not keep the state of a call in a policy.

#### `IRetryStrategy`
The strategy controls the number of attempts and the delay before each retry. A strategy has no state for a call. Thus, many calls can use one strategy at the same time.

There are two sets of strategies:

- The strategy classes (`Count`, `DoNotRetry`, `Linear`, `Incremental`, `Backoff` and `Exponential`). Their constructors count all the attempts, with a `maxTries` parameter.
- The methods of the static class `RetryStrategies`. These methods count retries, not attempts. The total number of attempts is the number of retries plus 1. A value of 0 for `retries` gives one attempt.

In the formulas, `n` is the number of the attempt that failed. The first attempt is 1. Thus, `n` is also the number of the retry that follows the delay.

| Strategy | Number of attempts | Delay before retry `n` |
|----------|--------------------|------------------------|
| `new Count(maxTries)` | `maxTries` (default 5) | `0` |
| `new DoNotRetry()` | 1 | No retry |
| `new Linear(d, maxTries)` | `maxTries` | `d` |
| `new Incremental(maxTries, initialDelay, step)` | `maxTries` (default 5) | `initialDelay + (n − 1) × step` (default: 1 s initial delay, 2 s step) |
| `new Backoff(maxTries, δ)` | `maxTries` (default 5) | `min(MaxBackoff, MinBackoff + (2^n − 1) × r)`, with `r` a random value from `0.8 × δ` to `1.2 × δ` (default: δ = 2 s, `MinBackoff` = 1 s, `MaxBackoff` = 30 s) |
| `new Exponential(maxTries, d)` | `maxTries` | `d × 2^(n−1)` |
| `RetryStrategies.Default` | 10 | `0` (a shared `Count(10)`) |
| `RetryStrategies.Constant(retries, d)` | `retries + 1` | `d` |
| `RetryStrategies.Linear(retries, d)` | `retries + 1` | `d × n` |
| `RetryStrategies.Exponential(retries, d)` | `retries + 1` | `d × 2^(n−1)` |
| `RetryStrategies.FromDelegate(getRetryDelay)` | The delegate decides | The value of the delegate, or `null` to stop |
| `RetryStrategies.RetryAfter(inner)` | The inner strategy decides | The `Retry-After` header of an `HttpResponseMessage` result, or `CircuitRejectedException.RetryAfter`. A header wins over a rejection. If there is no value, or the framework cannot parse the header, the delay of the inner strategy. A past date gives zero. The `maxDelay` of the inner strategy does not limit a header or rejection delay. |

> **Note:** The `Linear` class and the `RetryStrategies.Linear` method have different formulas. The delay of the `Linear` class is always `d`. The delay of `RetryStrategies.Linear` is `d × n`. `RetryStrategies.Constant` has the same formula as the `Linear` class.

`Linear`, `Incremental` and `Exponential` have constructor overloads with the optional parameters `maxDelay` and `useJitter`. The `RetryStrategies` methods `Constant`, `Linear` and `Exponential` have the same parameters:

- **Jitter for `Linear`, `Incremental`, `Constant` and `RetryStrategies.Linear`.** The strategy multiplies each delay by a random value from 0.75 to 1.25 (±25 %).
- **Jitter for `Exponential`.** The strategy uses decorrelated jitter: `min(cap, random(d, 3 × previous))`. The value `previous` is `RetryAttempt.PreviousDelay`, or `d` before the first retry. Thus, the first delay is from `d` to `3 × d`.
- **`maxDelay`.** The strategy applies the jitter first, and then limits the delay to `maxDelay`. The value `cap` is `maxDelay`. If `maxDelay` is shorter than `d`, every delay is `maxDelay`.

All strategies limit each delay to 0xFFFFFFFE ms (approximately 49.7 days). `Task.Delay` and the timer of a `CancellationTokenSource` do not accept a longer delay.

`Backoff` has `MinBackoff` and `MaxBackoff` properties. Set them with an object initialiser:

```csharp
Backoff backoff = new(maxTries: 5, deltaBackoff: TimeSpan.FromSeconds(1)) { MaxBackoff = TimeSpan.FromSeconds(10) };
```

A negative value gives an `ArgumentOutOfRangeException`. If `MinBackoff` is greater than `MaxBackoff`, `GetRetryDelay` throws an `ArgumentException`. The strategy examines the pair at the call, because an object initialiser can set the bounds in either order.

##### Custom strategy
An `IRetryStrategy` has one method: `TimeSpan? GetRetryDelay(in RetryAttempt attempt)`. The method returns the delay before the next attempt, or `null` to stop the retries.

`RetryAttempt` gives the data about the attempt that failed:

- `AttemptNumber` is the number of the attempt that failed. The first attempt is 1.
- `Exception` is the exception of the attempt. It is `null` when a result caused the retry.
- `Result` is the result that caused the retry, or `null`.
- `PreviousDelay` is the delay before the attempt that failed. It is zero for the first attempt.

A strategy must not keep the state of a call. The library keeps the attempt number and the previous delay for each call.

```csharp
public sealed class TimeoutAware : IRetryStrategy
{
    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= 5)
        {
            return null;
        }

        return attempt.Exception is RetryTimeoutException
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromMilliseconds(500);
    }
}
```

For a short strategy, use `RetryStrategies.FromDelegate`:

```csharp
IRetryStrategy strategy = RetryStrategies.FromDelegate(
    attempt => attempt.AttemptNumber < 4 ? TimeSpan.FromMilliseconds(100 * attempt.AttemptNumber) : null);
```

#### `TimeProvider`
The library uses `TimeProvider` for all delays and all timeouts.

- `RetryTaskFactory`, `RetryTaskFactory<T>`, `HedgedTaskFactory` and `HedgedTaskFactory<T>` have a `TimeProvider` property. `CircuitBreaker` has a `timeProvider` constructor parameter. A test can set a fake time provider, for example `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package. Then the test controls the time of the retries.
- `StartNew` uses the `TimeProvider` of its factory for its delays.
- `RetryStrategies.RetryAfter` has an optional `timeProvider` parameter for a `Retry-After` header with a date.
- `Retriable`, the default factories (`RetryTask.Factory`, `RetryTask<T>.Factory`, `HedgedTask.Factory` and `HedgedTask<T>.Factory`) and the restart delay of `ReliableTaskRunner` use `TimeProvider.System`. To use a different time provider, make a configured factory.

See [ADR 0002](Documentation/adr/0002-use-timeprovider-for-delays-and-timeouts.md).

### `ReliableTaskRunner`
`ReliableTaskRunner` runs an operation that continues until you stop it, for example a message processor. If the operation fails, the runner starts it again.

Start the operation with the static `ReliableTaskRunner.Run()` method. The runner implements `IAsyncDisposable`. Use `await using` to stop the operation at the end of the scope:

```csharp
await using ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => DoSomeOperationAsync(cancellationToken));
```

To stop the operation and get the outcome of the last run, call `StopAsync()`:

```csharp
await runner.StopAsync();
```

An overload of `Run` takes an `IRetryPolicy`, which controls the restarts.

- When a run fails, the runner gives each exception of the run to the policy (not the `AggregateException` around them). The runner starts the operation again only if the policy accepts all the exceptions.
- A run that cancels itself, for example because of an `HttpClient` timeout, is a failure. The runner gives the `OperationCanceledException` to the policy.
- The runner does not start the operation again after a run that completes, or after a call to `StopAsync()`.
- The runner starts each restart on the thread pool.
- The task from `StopAsync()` ends as the last run ended. If the run faulted, the task faults with all the exceptions of the run. If the policy or the restart strategy throws an exception, the runner stops, and the task faults with that exception.
- `StopAsync()` cancels the token with `CancelAsync`. Thus, the callbacks of the token run on a thread-pool thread, not on the thread that calls `StopAsync()`. If a callback throws, `StopAsync()` still waits until the run ends. Its task then faults with the exceptions of the callbacks, followed by the exceptions of the run if the run failed.
- `StopAsync()` and `DisposeAsync()` wait until a restart decision that is in progress ends.
- `DisposeAsync()` stops the operation, waits until the run ends, and releases the resources of the runner. It does not throw the exception of the run. A second call has no effect.
- If the run function returns a `null` task, `Run` throws an `InvalidOperationException`. On a restart, a `null` task is a run that failed with an `InvalidOperationException`.
- Do not await the task from `StopAsync()` in the run function. The task completes only when the run ends.

#### Wait between restarts

`Run(runFunction)` and `Run(runFunction, retryPolicy)` restart the operation immediately after each failure, with no limit.

> **CAUTION:** If the operation fails immediately at each start, these overloads restart it continuously, and use much processor time. To prevent this, use a restart strategy.

The overload `Run(runFunction, restartStrategy, retryPolicy)` waits for the delay of an `IRetryStrategy` before each restart:

```csharp
await using ReliableTaskRunner runner = ReliableTaskRunner.Run(
    cancellationToken => ProcessMessagesAsync(cancellationToken),
    new Exponential(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
    AnyExceptionPolicy.Default);
```

- After a run fails, the runner gives a `RetryAttempt` to the strategy. `AttemptNumber` is the number of runs that failed since the call to `Run`, from 1. `Exception` is the `AggregateException` of the run, or the `OperationCanceledException` of a run that cancelled itself.
- If the strategy returns `null`, the runner does not restart. If not, the policy decides. If the policy accepts the failure, the runner waits for the delay, and then restarts.
- A strategy with a limit stops the runner after that number of failed runs. For a runner that must continue, use a strategy with no limit, for example `new Exponential(int.MaxValue, ...)`.
- `StopAsync()` and `DisposeAsync()` stop a restart delay, and no restart occurs.
- The delay uses `TimeProvider.System`.

## Migrating from Polly

To replace Polly with Corvus.Retry, read the [migration guide from Polly](Documentation/Migrating-from-Polly.md).

## Samples

The folder [`Examples`](Examples) has sixteen demos, each a standalone [Spectre.Console](https://spectreconsole.net/) app in one file. Each demo shows one feature or one scenario, simulates the dependency it retries against, and renders what the library did: the attempts, the delays, the state of the circuit, or which hedged attempt won. The demos need only the .NET 10 SDK and no network access. Each demo uses the library source with `#:project`, thus it always uses the code in this repository.

Run the interactive launcher, every demo in order, or any demo directly, from the root of the repository:

    dotnet run Examples/menu.cs
    dotnet run Examples/menu.cs -- --all
    dotnet run Examples/http-client-resilience.cs

See the [demos README](Examples/README.md) for the full list. Each demo takes between 2 and 13 seconds after the first build.

## Benchmarks

`Solutions/Corvus.Retry.Benchmarks` uses [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure the time and the memory that the library adds. These classes measure every public type:

| Class | Public API that it measures | Baseline |
|-------|-----------------------------|----------|
| `RetryTaskFactoryBenchmarks` | `Run` and `RunValueTask` of `RetryTaskFactory` (with a result and with no result) and of `RetryTaskFactory<T>`, with no settings, each timeout, the two timeouts, `ContinueOnCapturedContext`, `OnRetry`, two failures, and a result retry | A hand-written retry loop |
| `CircuitBreakerBenchmarks` | A factory with a `CircuitBreaker`: a closed circuit, a failure, a rejection by an open or isolated circuit, and the test call of a half-open circuit | The same factory without a breaker |
| `FallbackBenchmarks` | A `RetryTaskFactory<T>` with a `Fallback`, when the operation succeeds, throws an exception, or returns a rejected result | The same factory without a fallback |
| `HedgingBenchmarks` | A `HedgedTaskFactory`: a success, a failure that starts a hedged attempt, and two attempts in parallel | The same operation through a `RetryTaskFactory` |
| `RetriableOverloadBenchmarks` | All the `Retriable.Retry` and `RetryAsync` overloads, with the token overloads and `onRetry`, when the first attempt succeeds | `Retry(func)`, `RetryAsync(func)` and the first token overload |
| `RetriableBenchmarks` | `Retry`, `RetryAsync` and the token overload (with and without `onRetry`), with 0 and 2 failures before a success | A hand-written retry loop |
| `RetryTaskFactoryOverloadBenchmarks` | All the `StartNew` overloads of `RetryTask.Factory` and `RetryTask<T>.Factory` (29 each), when the first attempt succeeds | |
| `RetryTaskBenchmarks` | `RetryTask<T>.Factory.StartNew` with 0 and 2 failures | `Task<T>.Factory.StartNew` with a hand-written loop |
| `ReliableTaskRunnerBenchmarks` | `ReliableTaskRunner.Run` (the two overloads without a restart strategy), `StopAsync` and `DisposeAsync`, with restarts after 1 and 4 failures | `Run` and then `StopAsync` |
| `StrategyBenchmarks` | `GetRetryDelay` of all the strategy classes and the `RetryStrategies` methods, with jitter, `maxDelay`, `RetryAfter` and `RetryStrategies.Default`, for 1 and 9 failures | `Count` |
| `PolicyBenchmarks` | `CanRetry` of `AnyExceptionPolicy`, `DoNotRetryPolicy`, the `RetryPolicies` methods, the extension members and `TransientHttp`, and `IsTransientHttpResponse` | The first item of each category |
| `AggregatePolicyBenchmarks` | `AggregatePolicy.CanRetry` with 1, 4 and 16 child policies | One policy |
| `DelayBenchmarks` | The delay of `Retriable.Retry` (`Thread.Sleep`) and of `Retriable.RetryAsync` (`Task.Delay` with `TimeProvider.System`) | The same retry with no delay |
| `ConstructionBenchmarks` | The public constructors and factory methods: the strategies, the `RetryStrategies` methods, the policies, the `RetryPolicies` methods and the extension members, `RetryEventArgs`, the exceptions, the event and attempt records, the retry factories, the hedged factories, `Fallback<T>` and `CircuitBreaker` | `new Count()`, `new AnyExceptionPolicy()`, `new RetryEventArgs(...)` and `new RetryTaskFactory()` |

No benchmark waits for time to pass. The strategies calculate delays, but the benchmarks do not wait for them. `DelayBenchmarks` uses a delay of one tick, which becomes 0 ms. Each timeout of a factory is too long to expire. The benchmarks call the `StartNew` overloads through the `RetryTaskFactoryDriver` of the test project. Thus, if a new overload has no benchmark, a test fails.

To run the benchmarks in Release, use this command from the root of the repository:

    dotnet run -c Release --project Solutions/Corvus.Retry.Benchmarks -- --filter "*"

A full run has many cases and takes more than one hour. Use `--filter` to select benchmarks (for example `"*RetryTaskFactory*"`), and `--job short` for a fast run. BenchmarkDotNet writes the results to `BenchmarkDotNet.Artifacts`. The times change between sessions. Thus, compare the ratios from one run, not the absolute times from different runs. The build compiles the benchmarks, but does not run them.

[Performance of Corvus.Retry 3.0](Documentation/Performance.md) gives the measured results, a comparison with version 2.0, and the causes of the slower results. This README does not repeat the figures.

## Building

The build uses [ZeroFailed](https://github.com/zerofailed/ZeroFailed). It must have PowerShell 7.4 or later and the .NET 10 SDK.
From the repo root, run:

    ./build.ps1

This restores, builds, tests (on Microsoft.Testing.Platform, with code coverage) and packages the solution
`Solutions/Corvus.Retry.slnx`. The build configuration lives in `.zf/config.ps1`. CI runs the same build in
GitHub Actions (`.github/workflows/build.yml`).

## Changelog

[CHANGELOG.md](CHANGELOG.md) gives the changes in each release, with the behaviour changes in 2.0 and 3.0. The [migration guide from 2.x to 3.0](Documentation/Migrating-from-2.x-to-3.0.md) gives each change that is not compatible with 2.x, and the 3.0 replacement.

## Licences

[![GitHub licence](https://img.shields.io/badge/License-Apache%202-blue.svg)](https://raw.githubusercontent.com/corvus-dotnet/Corvus.Retry/main/LICENSE)

Corvus.Retry is available under the Apache 2.0 open source licence.

For any licensing questions, please email [&#108;&#105;&#99;&#101;&#110;&#115;&#105;&#110;&#103;&#64;&#101;&#110;&#100;&#106;&#105;&#110;&#46;&#99;&#111;&#109;](&#109;&#97;&#105;&#108;&#116;&#111;&#58;&#108;&#105;&#99;&#101;&#110;&#115;&#105;&#110;&#103;&#64;&#101;&#110;&#100;&#106;&#105;&#110;&#46;&#99;&#111;&#109;)

## Project Sponsor

This project is sponsored by [endjin](https://endjin.com), a UK based Technology Consultancy which specialises in Data & Analytics, AI & Cloud Native App Dev.

> We help small teams achieve big things.

We produce three free weekly newsletters: 

 - [Azure Weekly](https://azureweekly.info) for all things about the Microsoft Azure Platform
 - [Power BI Weekly](https://powerbiweekly.info) for all things Power BI
 - [Fabric Weekly](https://fabricweekly.info) for all things Microsoft Fabric

Keep up with everything that's going on at endjin via our [blog](https://endjin.com/blog), follow us on [Twitter](https://twitter.com/endjin), [YouTube](https://www.youtube.com/c/endjin) or [LinkedIn](https://www.linkedin.com/company/endjin).

We have become the maintainers of a number of popular .NET Open Source Projects:

- [Reactive Extensions for .NET](https://github.com/dotnet/reactive)
- [Reaqtor](https://github.com/reaqtive)
- [Argotic Syndication Framework](https://github.com/argotic-syndication-framework/)

And we have over 50 Open Source projects of our own, spread across the following GitHub Orgs:

- [endjin](https://github.com/endjin/)
- [Corvus](https://github.com/corvus-dotnet)
- [Menes](https://github.com/menes-dotnet)
- [Marain](https://github.com/marain-dotnet)
- [AIS.NET](https://github.com/ais-dotnet)

And the DevOps tooling we have created for managing all these projects is available on the [PowerShell Gallery](https://www.powershellgallery.com/profiles/endjin).

For more information about our products and services, or for commercial support of this project, please [contact us](https://endjin.com/contact-us). 

## Code of conduct

This project has adopted a code of conduct adapted from the [Contributor Covenant](http://contributor-covenant.org/) to clarify expected behaviour in our community. This code of conduct has been [adopted by many other projects](http://contributor-covenant.org/adopters/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [&#104;&#101;&#108;&#108;&#111;&#064;&#101;&#110;&#100;&#106;&#105;&#110;&#046;&#099;&#111;&#109;](&#109;&#097;&#105;&#108;&#116;&#111;:&#104;&#101;&#108;&#108;&#111;&#064;&#101;&#110;&#100;&#106;&#105;&#110;&#046;&#099;&#111;&#109;) with any additional questions or comments.

## IP Maturity Matrix (IMM)

The IMM is endjin's IP quality framework.

[![Shared Engineering Standards](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/74e29f9b-6dca-4161-8fdd-b468a1eb185d?nocache=true)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/74e29f9b-6dca-4161-8fdd-b468a1eb185d?cache=false)

[![Coding Standards](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f6f6490f-9493-4dc3-a674-15584fa951d8?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f6f6490f-9493-4dc3-a674-15584fa951d8?cache=false)

[![Executable Specifications](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/bb49fb94-6ab5-40c3-a6da-dfd2e9bc4b00?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/bb49fb94-6ab5-40c3-a6da-dfd2e9bc4b00?cache=false)

[![Code Coverage](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/0449cadc-0078-4094-b019-520d75cc6cbb?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/0449cadc-0078-4094-b019-520d75cc6cbb?cache=false)

[![Benchmarks](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/64ed80dc-d354-45a9-9a56-c32437306afa?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/64ed80dc-d354-45a9-9a56-c32437306afa?cache=false)

[![Reference Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/2a7fc206-d578-41b0-85f6-a28b6b0fec5f?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/2a7fc206-d578-41b0-85f6-a28b6b0fec5f?cache=false)

[![Design & Implementation Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f026d5a2-ce1a-4e04-af15-5a35792b164b?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f026d5a2-ce1a-4e04-af15-5a35792b164b?cache=false)

[![How-to Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/145f2e3d-bb05-4ced-989b-7fb218fc6705?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/145f2e3d-bb05-4ced-989b-7fb218fc6705?cache=false)

[![Date of Last IP Review](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/da4ed776-0365-4d8a-a297-c4e91a14d646?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/da4ed776-0365-4d8a-a297-c4e91a14d646?cache=false)

[![Framework Version](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/6c0402b3-f0e3-4bd7-83fe-04bb6dca7924?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/6c0402b3-f0e3-4bd7-83fe-04bb6dca7924?cache=false)

[![Associated Work Items](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/79b8ff50-7378-4f29-b07c-bcd80746bfd4?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/79b8ff50-7378-4f29-b07c-bcd80746bfd4?cache=false)

[![Source Code Availability](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/30e1b40b-b27d-4631-b38d-3172426593ca?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/30e1b40b-b27d-4631-b38d-3172426593ca?cache=false)

[![Licence](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/d96b5bdc-62c7-47b6-bcc4-de31127c08b7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/d96b5bdc-62c7-47b6-bcc4-de31127c08b7?cache=false)

[![Production Use](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/87ee2c3e-b17a-4939-b969-2c9c034d05d7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/87ee2c3e-b17a-4939-b969-2c9c034d05d7?cache=false)

[![Insights](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/71a02488-2dc9-4d25-94fa-8c2346169f8b?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/71a02488-2dc9-4d25-94fa-8c2346169f8b?cache=false)

[![Packaging](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/547fd9f5-9caf-449f-82d9-4fba9e7ce13a?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/547fd9f5-9caf-449f-82d9-4fba9e7ce13a?cache=false)

[![Deployment](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/edea4593-d2dd-485b-bc1b-aaaf18f098f9?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/edea4593-d2dd-485b-bc1b-aaaf18f098f9?cache=false)


[![OpenChain](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/66efac1a-662c-40cf-b4ec-8b34c29e9fd7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/66efac1a-662c-40cf-b4ec-8b34c29e9fd7?cache=false)

