# Corvus.Retry
[![Build Status](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml/badge.svg)](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml)
[![GitHub licence](https://img.shields.io/badge/License-Apache%202-blue.svg)](https://raw.githubusercontent.com/corvus-dotnet/Corvus.Retry/main/LICENSE)
[![IMM](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)

This library retries operations that fail, and restarts long-running operations that fail.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

If you use version 2.x, read the [migration guide from 2.x to 3.0](Documentation/Migrating-from-2.x-to-3.0.md) before you do the upgrade.

## Features

The library has four types that retry an operation:

### `ReliabilityPipeline`
A pipeline holds all the retry settings in one immutable object. It adds timeouts, result retries, a fallback, a circuit breaker and hedging to the retries.
### `Retriable`
`Retriable` puts retries around a synchronous or an asynchronous operation, with a small set of parameters.
### `RetryTask`
`RetryTask` starts a task, as `Task.Factory.StartNew` does, and retries the operation of the task.
### `ReliableTaskRunner`
`ReliableTaskRunner` runs a long-running asynchronous operation. If the operation fails, the runner starts it again.

## Usage

### `ReliabilityPipeline`

Make one `ReliabilityPipeline` and keep it in a field. A pipeline is immutable, and many threads can use it at the same time. Each call has its own state.

```csharp
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    Policy = RetryPolicies.TransientHttp,
    AttemptTimeout = TimeSpan.FromSeconds(5),
});

string body = await pipeline.ExecuteAsync(
    ct => new ValueTask<string>(httpClient.GetStringAsync(uri, ct)),
    cancellationToken);
```

The operation receives a cancellation token. The pipeline cancels this token when the caller cancels, or when a timeout expires. Give the token to all the asynchronous methods that the operation calls.

#### Options

`ReliabilityPipelineOptions` is an immutable record. Use a `with` expression to make a changed copy. The constructor of the pipeline examines the options, and keeps their values.

| Property | Default | Description |
|----------|---------|-------------|
| `Strategy` | `null`: 4 attempts, with 2 seconds before each retry (`new Linear(TimeSpan.FromSeconds(2), 4)`) | The `IRetryStrategy` that gives the delay before each retry, or stops the retries. It must be `null` when `Hedging` is set. |
| `Policy` | `AnyExceptionPolicy.Default` | The `IRetryPolicy` that accepts or refuses each exception. It applies only to exceptions. |
| `AttemptTimeout` | `null` (no limit) | The time limit for each attempt. |
| `TotalTimeout` | `null` (no limit) | The time limit for all the attempts and all the delays. |
| `OnRetry` | `null` | A callback that the pipeline awaits before each delay. |
| `CircuitBreaker` | `null` | A shared `CircuitBreaker`. See [Circuit breaker](#circuit-breaker). |
| `Hedging` | `null` | The `HedgingOptions`. See [Hedging](#hedging). |
| `TimeProvider` | `TimeProvider.System` | The source of the time for the delays and the timeouts. |
| `ContinueOnCapturedContext` | `false` | If `true`, the pipeline continues on the captured context after each `await`. |

`ReliabilityPipelineOptions<T>` adds these properties for a `ReliabilityPipeline<T>`:

| Property | Default | Description |
|----------|---------|-------------|
| `ShouldRetryResult` | `null` | A predicate that returns `true` for a result that is a failure. |
| `Fallback` | `null` | The `FallbackOptions<T>`. See [Fallback](#fallback). |
| `HedgingActionGenerator` | `null` | A function that gives a different operation for each hedged attempt. See [Hedging](#hedging). |

The constructor throws an exception for an incorrect option. The `ParamName` of the exception is the name of the property:

- `ArgumentOutOfRangeException`: a timeout is zero, negative, or more than approximately 49.7 days. For no limit, use `null`, not `Timeout.InfiniteTimeSpan`.
- `ArgumentNullException`: `Policy` or `TimeProvider` is `null`.
- `ArgumentException`: `Hedging` and `Strategy` are set at the same time.
- `ArgumentException`, with the `ParamName` `options`: the non-generic `ReliabilityPipeline` received a `ReliabilityPipelineOptions<T>` that sets `ShouldRetryResult`, `Fallback` or `HedgingActionGenerator`. Only `ReliabilityPipeline<T>` can use these settings.

To make one attempt and no retry, set `Strategy` to `new Count(1)`, `new DoNotRetry()`, or a `RetryStrategies` method with `retries: 0`.

#### Timeouts and cancellation

The pipeline has two time limits:

- **Attempt timeout.** When `AttemptTimeout` expires, the pipeline cancels the token of the attempt. The attempt then fails with a `RetryTimeoutException`. The strategy and the policy decide if the pipeline retries it.
- **Total timeout.** When `TotalTimeout` expires, the pipeline cancels the token of the attempt, or stops the delay. No other attempt starts, and the call throws a `RetryTimeoutException`.

`RetryTimeoutException` derives from `TimeoutException`. Its `Timeout` property gives the time limit that expired. When the total timeout expires between two attempts, the inner exception is the exception of the last attempt.

The two timeouts are cooperative. The pipeline cancels the token, and then waits until the operation stops.

When the caller cancels the token, the call throws an `OperationCanceledException` that carries the caller's token. The pipeline does not retry this exception, and does not give it to the policy. The token also stops a delay immediately.

If the operation throws an `AggregateException`, the pipeline asks the policy about each inner exception, depth first, and retries only if the policy accepts all of them. An `AggregateException` with no inner exceptions goes to the policy itself. The strategy and `OnRetry` receive the `AggregateException` as thrown.

```csharp
ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromSeconds(1)),
    Policy = RetryPolicies.TransientHttp,
    AttemptTimeout = TimeSpan.FromSeconds(10),
    TotalTimeout = TimeSpan.FromSeconds(30),
});

using CancellationTokenSource cancellation = new();
try
{
    string body = await pipeline.ExecuteAsync(
        async ct => await httpClient.GetStringAsync(uri, ct),
        cancellation.Token);
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

> **Note:** A policy for one exception type does not retry attempt timeouts. An attempt timeout gives a `RetryTimeoutException`. To retry attempt timeouts, the policy must also accept `RetryTimeoutException` or `TimeoutException`, for example `RetryPolicies.Handle<HttpRequestException>().Or<TimeoutException>()`. `RetryPolicies.TransientHttp` and `AnyExceptionPolicy` accept `RetryTimeoutException`.

#### Operations that return a `Task`

The pipeline has only `ValueTask` overloads. With `Task` overloads, the compiler cannot select an overload for an `async` lambda (error CS0121). For a method that returns a `Task<T>`, use one of these lambdas:

```csharp
ct => new ValueTask<string>(httpClient.GetStringAsync(uri, ct))
async ct => await httpClient.GetStringAsync(uri, ct)
```

For a method that returns a `Task`, use `ct => new ValueTask(DoAsync(ct))`.

#### The retry callback

The pipeline awaits `OnRetry` before each delay. The callback receives a `RetryEventArgs`:

- `AttemptNumber` is the number of the attempt that failed. The first attempt is 1.
- `NextAttemptNumber` is the number of the attempt that starts after the delay.
- `LastException` is the exception of the attempt. It is `null` when a result caused the retry.
- `Result` is the result that caused the retry, or `null`.
- `DelayBeforeRetry` is the delay before the next attempt.

The pipeline does not call `OnRetry` for the last failure. An exception from the callback stops the call.

```csharp
ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.Linear(retries: 3, delay: TimeSpan.FromSeconds(1)),
    OnRetry = args =>
    {
        Console.WriteLine($"Attempt {args.AttemptNumber} failed. Attempt {args.NextAttemptNumber} starts after {args.DelayBeforeRetry}.");
        return ValueTask.CompletedTask;
    },
});
```

#### Retry a result

`ReliabilityPipeline<T>` can also retry a result. If `ShouldRetryResult` returns `true`, the pipeline uses the result as a failure. The strategy, the callback and the delay then apply. The policy applies only to exceptions.

- Before the next attempt, the pipeline disposes a rejected result that implements `IDisposable`. It does this after the callback.
- If the call stops with an exception after the operation returned a result, the pipeline also disposes that result. For example, the caller cancels, the total timeout expires, or `ShouldRetryResult`, the strategy or `OnRetry` throws an exception.
- If no retries remain, the pipeline returns the last result. It does not throw an exception.

`RetryStrategies.RetryAfter` uses the `Retry-After` header of an `HttpResponseMessage` result for the delay:

```csharp
ReliabilityPipeline<HttpResponseMessage> pipeline = new(new ReliabilityPipelineOptions<HttpResponseMessage>
{
    Strategy = RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(1))),
    Policy = RetryPolicies.TransientHttp,
    ShouldRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(10),
});

using HttpResponseMessage response = await pipeline.ExecuteAsync(
    ct => new ValueTask<HttpResponseMessage>(httpClient.GetAsync(uri, ct)),
    cancellationToken);
```

#### Order of the parts

The pipeline applies its parts in this sequence, from the outermost part to the innermost part:

```text
ExecuteAsync(operation, cancellationToken)
 └─ 1. Fallback                    ReliabilityPipelineOptions<T>.Fallback
     └─ 2. Total timeout           TotalTimeout
         └─ 3. Retry, or hedging   Strategy, Policy, OnRetry, ShouldRetryResult; or Hedging
             └─ 4. Circuit breaker CircuitBreaker
                 └─ 5. Attempt timeout  AttemptTimeout
                     └─ 6. The operation
```

Each part is optional. If an option is `null`, the pipeline does not use that part, and does not allocate memory for it. These are the results of the sequence:

- The fallback receives the outcome of the attempts. This outcome can be a `RetryTimeoutException` or a `BrokenCircuitException`.
- The total timeout includes all the attempts and all the delays.
- The circuit breaker counts each attempt as one call. It counts an attempt timeout as a failure.

#### Fallback

A fallback gives a value when the pipeline fails. It applies only to `ReliabilityPipeline<T>`.

| `FallbackOptions<T>` property | Default | Description |
|-------------------------------|---------|-------------|
| `Action` | None (`required`) | The function that gives the fallback value. |
| `Policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that get the fallback value. |
| `ShouldFallbackResult` | `null` | A predicate that returns `true` for a result that gets the fallback value. |
| `OnFallback` | `null` | A callback that the pipeline awaits before it calls `Action`. |

```csharp
ReliabilityPipeline<string> pipeline = new(new ReliabilityPipelineOptions<string>
{
    Strategy = RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(500)),
    TotalTimeout = TimeSpan.FromSeconds(5),
    Fallback = new FallbackOptions<string>
    {
        Action = args => ValueTask.FromResult("cached value"),
        OnFallback = args =>
        {
            Console.WriteLine($"Fallback after {args.Exception?.GetType().Name}.");
            return ValueTask.CompletedTask;
        },
    },
});
```

- If the pipeline throws an exception that `Policy` accepts, the pipeline calls `OnFallback` and then `Action`. It returns the value of `Action`.
- If `ShouldFallbackResult` returns `true` for a result, the pipeline disposes that result if it implements `IDisposable`. Then it calls `OnFallback` and `Action`.
- The fallback does not apply when the caller cancels the token.
- `FallbackArgs<T>` gives the `Exception` or the `Result`, and the `CancellationToken` of the caller. The total timeout does not apply to the fallback.
- An exception from `OnFallback` or from `Action` stops the call. The pipeline does not catch it.
- The fallback applies only to the outcome of the attempts: the exception or the result of the last attempt, a `RetryTimeoutException`, or a rejection by the circuit breaker. An exception from `ShouldRetryResult`, `OnRetry`, the strategy, a policy, `OnStateChanged`, `OnHedging` or `HedgingActionGenerator` stops the call. The fallback does not give a value for it.

#### Circuit breaker

A circuit breaker stops calls to a service that fails frequently. One `CircuitBreaker` instance holds the state. Many pipelines and threads can use the same instance.

| `CircuitBreakerOptions` property | Default | Description |
|----------------------------------|---------|-------------|
| `FailureRatio` | `0.1` | The ratio of failures to calls at which the circuit opens. The value must be more than 0, and 1 or less. |
| `MinimumThroughput` | `100` | The minimum number of calls in the window before the circuit can open. The minimum value is 2. |
| `SamplingDuration` | 30 seconds | The length of the sliding window that counts the calls and the failures. The minimum value is 500 ms. |
| `BreakDuration` | 5 seconds | The time for which the circuit stays open. The minimum value is 500 ms. |
| `Policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that are failures. |
| `OnStateChanged` | `null` | A callback for each change of state. |
| `TimeProvider` | `TimeProvider.System` | The source of the time for the window and the break. |

```csharp
CircuitBreaker breaker = new(new CircuitBreakerOptions
{
    FailureRatio = 0.5,
    MinimumThroughput = 10,
    BreakDuration = TimeSpan.FromSeconds(15),
    OnStateChanged = change =>
    {
        Console.WriteLine($"The circuit changed from {change.From} to {change.To}.");
        return ValueTask.CompletedTask;
    },
});

ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.RetryAfter(RetryStrategies.Constant(retries: 3, delay: TimeSpan.FromSeconds(1))),
    Policy = RetryPolicies.TransientHttp.Or(RetryPolicies.Handle<BrokenCircuitException>()),
    CircuitBreaker = breaker,
    AttemptTimeout = TimeSpan.FromSeconds(5),
});
```

The circuit has four states (`CircuitState`):

- **Closed.** The breaker counts the calls and the failures in a sliding window of `SamplingDuration`, with 10 buckets. If the window has `MinimumThroughput` calls or more, and the ratio of failures is `FailureRatio` or more, the circuit opens.
- **Open.** The breaker rejects each call immediately with a `BrokenCircuitException`. After `BreakDuration`, the circuit goes to half-open.
- **HalfOpen.** The breaker lets one test call through, and rejects the other calls. If the test call succeeds, the circuit closes and the counts start again. If the test call fails, the circuit opens again.
- **Isolated.** `Isolate()` sets this state. The breaker rejects each call with an `IsolatedCircuitException` until you call `Close()`. `Close()` closes the circuit and clears the counts.

These rules apply to the breaker:

- A failure is an exception that the `Policy` of the breaker accepts, or a result that `ShouldRetryResult` rejects. For an `AggregateException`, the breaker asks the `Policy` about each inner exception, and the call is a failure only if the policy accepts all of them. `CircuitStateChange.LastException` is the exception as thrown.
- An attempt timeout is always a failure. The breaker does not ask its `Policy` about a `RetryTimeoutException`. Thus, a `Policy` such as `RetryPolicies.Handle<HttpRequestException>()` does not make the breaker ignore timeouts.
- If the `Policy` of the breaker throws an exception, the call throws that exception. The breaker does not count the call. If the call was the test call of a half-open circuit, the next call can be the test call.
- A cancellation by the caller is not a call and not a failure. A call that the breaker rejects is not a call.
- A test call that started before `Isolate()`, `Close()` or a subsequent break does not change the state of the circuit. A test call that ends after `Close()` is not counted in the new window.
- The circuit goes from `Open` to `HalfOpen` on the first call after the break, not at the end of the break. Until that call, `State` gives `Open`.
- The breaker measures time with the monotonic timestamp of its `TimeProvider`. A change to the system clock does not change the window or the break.
- The breaker calls `OnStateChanged` after it releases its lock. A pipeline awaits the callback. `Isolate()` and `Close()` wait until the callback completes, thus the callback must be fast. They run the callback with no `SynchronizationContext`. Thus, a callback that awaits on a UI thread does not cause a deadlock. An exception from the callback stops the call.

The breaker is inside the retry part. Thus, a rejection is a failure of one attempt, and the strategy and the policy decide if the pipeline retries it:

- `AnyExceptionPolicy` retries a `BrokenCircuitException`. `RetryPolicies.TransientHttp` does not retry it.
- `BrokenCircuitException.RetryAfter` gives the time until the break ends. It is `null` when the circuit is half-open or isolated.
- `RetryStrategies.RetryAfter` uses `BrokenCircuitException.RetryAfter` as the delay. Thus, the next attempt starts when the break ends.

#### Hedging

Hedging starts more attempts in parallel when an attempt is slow or fails. The first acceptable outcome wins.

| `HedgingOptions` property | Default | Description |
|---------------------------|---------|-------------|
| `MaxHedgedAttempts` | `1` | The maximum number of attempts after the first attempt. The range is 1 thru 10. The total number of attempts is `MaxHedgedAttempts` + 1. |
| `Delay` | 2 seconds | The time after the start of an attempt at which the next attempt starts. `TimeSpan.Zero` starts all the attempts at the same time. `Timeout.InfiniteTimeSpan` starts the next attempt only after a failure. |
| `Policy` | `AnyExceptionPolicy.Default` | The policy that selects the exceptions that are failures. |
| `OnHedging` | `null` | A callback that the pipeline awaits before it starts each attempt after the first. |

```csharp
ReliabilityPipeline<string> pipeline = new(new ReliabilityPipelineOptions<string>
{
    Hedging = new HedgingOptions { MaxHedgedAttempts = 2, Delay = TimeSpan.FromMilliseconds(300) },
    AttemptTimeout = TimeSpan.FromSeconds(5),
    HedgingActionGenerator = attemptNumber => attemptNumber == 1
        ? null
        : ct => new ValueTask<string>(httpClient.GetStringAsync(secondaryUri, ct)),
});

string body = await pipeline.ExecuteAsync(
    ct => new ValueTask<string>(httpClient.GetStringAsync(uri, ct)),
    cancellationToken);
```

- Hedging replaces the retry part of the pipeline. If `Hedging` is set, `Strategy` must be `null`. The pipeline does not use `OnRetry` or the retry `Policy`.
- The pipeline starts attempt 1. It starts the next attempt when `Delay` expires, or immediately when an attempt fails.
- Attempt 1 runs on the thread of the caller until its first `await`. Attempts 2 and higher start on the thread pool, with no `SynchronizationContext`. Thus, an attempt that blocks its thread before its first `await` does not delay the next attempt, the cancellation or the total timeout. An attempt 1 that blocks also blocks the caller.
- A failure is an exception that `Hedging.Policy` accepts, or a result for which `ShouldRetryResult` returns `true`. An exception that `Hedging.Policy` refuses is not a failure: the pipeline throws it immediately. For an `AggregateException`, the policy is asked about each inner exception; `OnHedging` and the caller receive the aggregate as thrown.
- The first outcome that is not a failure wins. The pipeline returns it immediately, and cancels the token of each other attempt. It does not wait for the cancelled attempts, and it disposes their results if they implement `IDisposable`.
- The pipeline does not start an attempt after it received an outcome that wins. With a `Delay` of zero, an attempt 1 that wins synchronously stops all the other attempts. An attempt 2 or higher completes on the thread pool, thus the pipeline can start more attempts before it receives that outcome.
- `OnHedging` can run for an attempt that then does not start, because an outcome won during the callback. With a `Delay` of zero, if attempt 1 fails synchronously, `OnHedging` for attempt 2 receives that failure in `LastException`.
- If all the attempts fail, the pipeline throws the last exception, or returns the last result. When the total timeout expires, the `RetryTimeoutException` has the exception of the last attempt that failed as its inner exception, or `null` if the last outcome was a rejected result or no attempt has failed.
- `HedgingActionGenerator` receives the attempt number, from 1. It can give a different operation for each attempt, for example a different endpoint. If it returns `null`, the attempt uses the operation that you give to `ExecuteAsync`.
- Each attempt goes through the circuit breaker, and has its own attempt timeout. The total timeout and the fallback apply to all the attempts. A call that the caller cancels, or that `OnHedging` or a refused exception stops, releases the half-open test call before `ExecuteAsync` throws, thus a caller who retries at once makes the next test call. When the total timeout stops the call, each attempt still in flight is a failed call for the breaker.
- When the caller cancels or the total timeout expires, the hedged call stops immediately, also if an attempt ignores its token. (A pipeline that retries waits until the attempt stops.) A cancellation by the caller gives an `OperationCanceledException` that carries the caller's token.

The operation must be idempotent. It must also be safe when two or more attempts run at the same time.

A pipeline cannot use hedging and retries together. To retry a hedged call, use two pipelines. Run the hedging pipeline in the operation of the retry pipeline:

```csharp
string body = await retryPipeline.ExecuteAsync(
    ct => hedgingPipeline.ExecuteAsync(c => new ValueTask<string>(httpClient.GetStringAsync(uri, c)), ct),
    cancellationToken);
```

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

- `Retry` or `RetryTask.Factory.StartNew` with an operation that returns a `Task`, a `Task<T>`, a `ValueTask` or a `ValueTask<T>`. For a `ValueTask`, use `ReliabilityPipeline.ExecuteAsync`, or call `AsTask()` in the operation.
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

`Retriable` awaits `onRetry` before each delay. The synchronous `Retry` overloads wait for the callback. They run it with no `SynchronizationContext`, thus a callback that awaits continues on the thread pool. An exception from the callback stops the call.

`Retriable` has no timeouts and no result retries. For these functions, use `ReliabilityPipeline`.

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
| `RetryPolicies.Handle<TException>(predicate)` | An exception of type `TException` (or a derived type). The predicate is optional. Add more types with `.Or<T>()`. |
| `RetryPolicies.HandleInner<TException>(predicate)` | An exception of type `TException` in the exception, its `InnerException` chain, or the inner exceptions of an `AggregateException`, depth first |
| `RetryPolicies.Not(policy)` | An exception that `policy` refuses |
| `RetryPolicies.Any(policies)` | An exception that one or more of the policies accept |
| `RetryPolicies.All(policies)` | An exception that all the policies accept |
| `RetryPolicies.TransientHttp` | An `HttpRequestException` with no status code, or with status 408, 429, or 500 or more. Also a `RetryTimeoutException`. |

The C# 14 extension members `policy.Or(other)`, `policy.And(other)` and `policy.Not()` combine the policies that you have.

```csharp
IRetryPolicy policy = RetryPolicies.Handle<HttpRequestException>(ex => ex.StatusCode != HttpStatusCode.NotFound)
    .Or<RetryTimeoutException>()
    .Or(RetryPolicies.HandleInner<SocketException>())
    .And(RetryPolicies.Handle<ArgumentException>().Not());
```

`RetryPolicies.TransientHttp` refuses a `TaskCanceledException`, a `TimeoutException` that is not a `RetryTimeoutException`, and a `BrokenCircuitException`.

`RetryPolicies.IsTransientHttpResponse(response)` returns `true` for a status of 408, 429, or 500 or more. Use it as `ShouldRetryResult` in a `ReliabilityPipeline<HttpResponseMessage>`.

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
| `RetryStrategies.RetryAfter(inner)` | The inner strategy decides | The `Retry-After` header of an `HttpResponseMessage` result, or `BrokenCircuitException.RetryAfter`. A header wins over a rejection. If there is no value, the delay of the inner strategy. The `maxDelay` of the inner strategy does not limit a header or rejection delay. |

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

- `ReliabilityPipeline` and `CircuitBreaker` get the `TimeProvider` from their options. A test can set a fake time provider, for example `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package. Then the test controls the time of the retries.
- `RetryStrategies.RetryAfter` has an optional `timeProvider` parameter for a `Retry-After` header with a date.
- `Retriable`, `RetryTask` and the restart delay of `ReliableTaskRunner` use `TimeProvider.System`. They have no parameter for a different time provider.

See [ADR 0002](Documentation/adr/0002-use-timeprovider-for-delays-and-timeouts.md).

### `RetryTask`

`RetryTask` is a `Task` factory with retries. It replaces `TaskFactory` directly. Call `RetryTask.Factory.StartNew()` or `RetryTask<T>.Factory.StartNew()` with the same parameters that you give to `Task.Factory.StartNew()`.

```csharp
Task<string> task = RetryTask<string>.Factory.StartNew(() => File.ReadAllText("settings.json"), new Count(5), AnyExceptionPolicy.Default);
```

You can also give an `IRetryPolicy` and an `IRetryStrategy`. The default strategy is `new Count()` (5 attempts) and the default policy is `AnyExceptionPolicy`. These defaults are different from the defaults of `Retriable`.

- A retry does not block a thread. A delay task and continuations start each retry on the scheduler of the caller.
- If the operation stops, the task faults with the failures that the operation recorded. `RetryTask` keeps a maximum of 100 failures: the first 99 failures and the last failure.
- A cancellation gives a `Canceled` task.
- The returned task obeys `TaskCreationOptions.RunContinuationsAsynchronously`.
- `RetryTask` has no retry callback.
- `RetryTask` retries only synchronous operations. `RetryTask<T>.Factory.StartNew` throws a `NotSupportedException` when `T` is a `Task`, a `Task<TResult>`, a `ValueTask` or a `ValueTask<TResult>`. When `T` is `object` or an interface, `StartNew` examines the result of each try: if the function returned a `Task` or a `ValueTask`, the task faults with `NotSupportedException`. For an asynchronous operation, use `Retriable.RetryAsync`.
- If the scheduler refuses the first try, `StartNew` throws the `TaskSchedulerException`. If it refuses a retry, the refusal is a failure of that try, and the strategy and the policy decide.

The overloads that take a state object use the same nullable annotations as `Task.Factory.StartNew()`: the state is `object?`, and the action or function takes an `object?` argument. Version 1 used non-nullable `object`. If your project uses nullable reference types, change each method or delegate that takes `object`. It must take `object?`, or examine the state for `null` before it uses the state:

```csharp
RetryTask.Factory.StartNew(state => Process((string)state!), "input", new Count(5), new AnyExceptionPolicy());
```

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

This table gives the Corvus.Retry 3.0 item for each Polly 8 item:

| Polly 8 | Corvus.Retry 3.0 |
|---|---|
| `ResiliencePipeline`, built one time | `ReliabilityPipeline` and `ReliabilityPipelineOptions`, built one time |
| `ExecuteAsync(ct => …, token)` | `ExecuteAsync(ct => …, token)`, with the same `ValueTask` signature |
| `AddTimeout` in `AddRetry` | `AttemptTimeout` |
| `AddTimeout` around `AddRetry` | `TotalTimeout` |
| `TimeoutRejectedException` | `RetryTimeoutException` (also a `TimeoutException`) |
| `MaxRetryAttempts = n` | `retries: n` in the `RetryStrategies` methods |
| `DelayBackoffType.Constant` | `RetryStrategies.Constant` |
| `DelayBackoffType.Linear` | `RetryStrategies.Linear` |
| `DelayBackoffType.Exponential` | `RetryStrategies.Exponential` |
| `MaxDelay` | `maxDelay` |
| `UseJitter` | `useJitter`. For exponential delays, Corvus uses decorrelated jitter. The first delay is between `base` and `3 × base`. |
| `DelayGenerator` | `RetryStrategies.FromDelegate`, or `RetryStrategies.RetryAfter` for the `Retry-After` header |
| `PredicateBuilder().Handle<T>()` | `RetryPolicies.Handle<T>().Or<T2>()` |
| `PredicateBuilder<T>().HandleResult(…)` | `ReliabilityPipelineOptions<T>.ShouldRetryResult` |
| `HandleInner<T>()` | `RetryPolicies.HandleInner<T>()` |
| `HttpClientResiliencePredicates.IsTransient` | `RetryPolicies.TransientHttp` and `RetryPolicies.IsTransientHttpResponse` |
| `AddFallback` | `ReliabilityPipelineOptions<T>.Fallback` |
| `AddCircuitBreaker` | `ReliabilityPipelineOptions.CircuitBreaker`, with one shared `CircuitBreaker` |
| `BrokenCircuitException`, `IsolatedCircuitException` | `BrokenCircuitException`, `IsolatedCircuitException` |
| `CircuitBreakerManualControl` | `CircuitBreaker.Isolate()` and `CircuitBreaker.Close()` |
| `AddHedging` | `ReliabilityPipelineOptions.Hedging` |
| `ActionGenerator` (hedging) | `ReliabilityPipelineOptions<T>.HedgingActionGenerator` |
| `OnRetry`, `AttemptNumber` starts at 0 | `OnRetry`, `AttemptNumber` starts at 1 |
| `TimeProvider` | `TimeProvider` |

These are the primary differences:

- Polly uses a builder. Corvus.Retry uses an options record. The constructor of the pipeline examines all the options.
- Polly adds the parts in the sequence of the builder calls. Corvus.Retry always uses the sequence in [Order of the parts](#order-of-the-parts).
- In Polly, `AttemptNumber` in `OnRetry` starts at 0. In Corvus.Retry, `AttemptNumber` is the number of the attempt that failed, and starts at 1.
- A Corvus.Retry pipeline cannot use hedging and retries together. Use two pipelines.
- The Corvus.Retry fallback applies only to the outcome of the attempts. An exception from a callback, a strategy or a policy of the pipeline, for example from `OnRetry`, stops the call. The fallback does not give a value for it.

This example shows a Polly pipeline with a total timeout, a retry and an attempt timeout:

```csharp
// Polly 8
ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(30))
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TimeoutRejectedException>(),
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(200),
        UseJitter = true,
        OnRetry = args =>
        {
            Console.WriteLine($"Attempt {args.AttemptNumber + 1} failed.");
            return default;
        },
    })
    .AddTimeout(TimeSpan.FromSeconds(5))
    .Build();

string body = await pipeline.ExecuteAsync(
    async ct => await httpClient.GetStringAsync(uri, ct),
    cancellationToken);
```

This is the equivalent Corvus.Retry pipeline:

```csharp
// Corvus.Retry 3.0
ReliabilityPipeline pipeline = new(new ReliabilityPipelineOptions
{
    TotalTimeout = TimeSpan.FromSeconds(30),
    Strategy = RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    Policy = RetryPolicies.Handle<HttpRequestException>().Or<RetryTimeoutException>(),
    OnRetry = args =>
    {
        Console.WriteLine($"Attempt {args.AttemptNumber} failed.");
        return ValueTask.CompletedTask;
    },
    AttemptTimeout = TimeSpan.FromSeconds(5),
});

string body = await pipeline.ExecuteAsync(
    async ct => await httpClient.GetStringAsync(uri, ct),
    cancellationToken);
```

## Samples

The folder `Examples` has samples. Each sample is a .NET 10 file-based app. Each sample uses the library source with `#:project`, thus it always uses the code in this repository. To run a sample, use one of these commands from the root of the repository:

    dotnet run Examples/ReliabilityPipelineSample.cs
    dotnet run Examples/RetriableSample.cs
    dotnet run Examples/ReliableTaskRunnerSample.cs
    dotnet run Examples/RetryTaskSample.cs

The samples use the .NET 10 SDK. `RetriableSample` sends pings to `endjin.com`, thus it must have network access. The other samples do not use the network. Some samples wait between the retries: `RetriableSample` takes approximately 2 minutes, and the other samples take approximately 20 seconds each.

## Benchmarks

`Solutions/Corvus.Retry.Benchmarks` uses [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure the time and the memory that the library adds. These classes measure every public type:

| Class | Public API that it measures | Baseline |
|-------|-----------------------------|----------|
| `ReliabilityPipelineBenchmarks` | `ReliabilityPipeline.ExecuteAsync` (with a result and with no result) and `ReliabilityPipeline<T>.ExecuteAsync`, with no options, each timeout, the two timeouts, `ContinueOnCapturedContext`, `OnRetry`, two failures, and a result retry | A hand-written retry loop |
| `CircuitBreakerBenchmarks` | A pipeline with a `CircuitBreaker`: a closed circuit, a failure, a rejection by an open or isolated circuit, and the test call of a half-open circuit | The same pipeline without a breaker |
| `FallbackBenchmarks` | A `ReliabilityPipeline<T>` with `Fallback`, when the operation succeeds, throws an exception, or returns a rejected result | The same pipeline without a fallback |
| `HedgingBenchmarks` | A pipeline with `Hedging`: a success, a failure that starts a hedged attempt, and two attempts in parallel | The same operation through a pipeline that retries |
| `RetriableOverloadBenchmarks` | All the `Retriable.Retry` and `RetryAsync` overloads, with the token overloads and `onRetry`, when the first attempt succeeds | `Retry(func)`, `RetryAsync(func)` and the first token overload |
| `RetriableBenchmarks` | `Retry`, `RetryAsync` and the token overload (with and without `onRetry`), with 0 and 2 failures before a success | A hand-written retry loop |
| `RetryTaskFactoryOverloadBenchmarks` | All the `StartNew` overloads of `RetryTask.Factory` and `RetryTask<T>.Factory` (29 each), when the first attempt succeeds | |
| `RetryTaskBenchmarks` | `RetryTask<T>.Factory.StartNew` with 0 and 2 failures | `Task<T>.Factory.StartNew` with a hand-written loop |
| `ReliableTaskRunnerBenchmarks` | `ReliableTaskRunner.Run` (the two overloads without a restart strategy), `StopAsync` and `DisposeAsync`, with restarts after 1 and 4 failures | `Run` and then `StopAsync` |
| `StrategyBenchmarks` | `GetRetryDelay` of all the strategy classes and the `RetryStrategies` methods, with jitter, `maxDelay`, `RetryAfter` and `RetryStrategies.Default`, for 1 and 9 failures | `Count` |
| `PolicyBenchmarks` | `CanRetry` of `AnyExceptionPolicy`, `DoNotRetryPolicy`, the `RetryPolicies` methods, the extension members and `TransientHttp`, and `IsTransientHttpResponse` | The first item of each category |
| `AggregatePolicyBenchmarks` | `AggregatePolicy.CanRetry` with 1, 4 and 16 child policies | One policy |
| `DelayBenchmarks` | The delay of `Retriable.Retry` (`Thread.Sleep`) and of `Retriable.RetryAsync` (`Task.Delay` with `TimeProvider.System`) | The same retry with no delay |
| `ConstructionBenchmarks` | The public constructors and factory methods: the strategies, the `RetryStrategies` methods, the policies, the `RetryPolicies` methods and the extension members, `RetryEventArgs`, the exceptions, the event and attempt records, the options records, the two factories, the pipelines and `CircuitBreaker` | `new Count()`, `new AnyExceptionPolicy()`, `new RetryEventArgs(...)` and `new ReliabilityPipelineOptions()` |

No benchmark waits for time to pass. The strategies calculate delays, but the benchmarks do not wait for them. `DelayBenchmarks` uses a delay of one tick, which becomes 0 ms. Each pipeline timeout is too long to expire. The benchmarks call the `StartNew` overloads through the `RetryTaskFactoryDriver` of the test project. Thus, if a new overload has no benchmark, a test fails.

To run the benchmarks in Release, use this command from the root of the repository:

    dotnet run -c Release --project Solutions/Corvus.Retry.Benchmarks -- --filter "*"

A full run has many cases and takes more than one hour. Use `--filter` to select benchmarks (for example `"*ReliabilityPipeline*"`), and `--job short` for a fast run. BenchmarkDotNet writes the results to `BenchmarkDotNet.Artifacts`. The times change between sessions. Thus, compare the ratios from one run, not the absolute times from different runs. The build compiles the benchmarks, but does not run them.

[Performance of Corvus.Retry 3.0](Documentation/Performance.md) gives the results, a comparison with version 2.0, and the causes of the slower results.

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

