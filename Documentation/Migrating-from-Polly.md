# Migrating from Polly to Corvus.Retry 3.0

This guide tells you how to replace Polly with Corvus.Retry 3.0. It gives the Corvus.Retry item for each Polly item, the primary differences, and three worked examples.

Corvus.Retry does not copy the API of Polly. It follows the `TaskFactory` of .NET: you make a configured `RetryTaskFactory`, keep it in a field, and call its `Run` method. The [README](../README.md) tells how to use each type. [ADR 0003](adr/0003-follow-taskfactory-not-a-pipeline.md) gives the reasons for this design.

## Concept map

The examples use Polly 8 names, except where a row names Polly 7.

| Polly | Corvus.Retry 3.0 |
|-------|------------------|
| `ResiliencePipelineBuilder` and `ResiliencePipeline`, built one time | A configured `RetryTaskFactory` or `RetryTaskFactory<T>`, made one time and kept in a field |
| `ExecuteAsync(ct => …, token)` | `Run(ct => …, token)` for an operation that returns a `Task`. `RunValueTask(ct => …, token)` for an operation that returns a `ValueTask`. |
| `Execute(() => …)` (synchronous) | `Retriable.Retry(() => …, CancellationToken.None, strategy, policy)`. See [Synchronous calls](#synchronous-calls). |
| `AddTimeout` inside `AddRetry` | `AttemptTimeout` |
| `AddTimeout` outside `AddRetry` | `TotalTimeout` |
| `TimeoutRejectedException` | `RetryTimeoutException` (also a `TimeoutException`) |
| `MaxRetryAttempts = n` | `retries: n` in the `RetryStrategies` methods |
| `DelayBackoffType.Constant` | `RetryStrategies.Constant` |
| `DelayBackoffType.Linear` | `RetryStrategies.Linear` |
| `DelayBackoffType.Exponential` | `RetryStrategies.Exponential` |
| `MaxDelay` | `maxDelay` |
| `UseJitter` | `useJitter`. For exponential delays, Corvus.Retry uses decorrelated jitter. The first delay is from `base` to `3 × base`. |
| `DelayGenerator` | `RetryStrategies.FromDelegate`, or `RetryStrategies.RetryAfter` for the `Retry-After` header |
| `new PredicateBuilder().Handle<T>().Handle<T2>()` | `RetryPolicies.For<T>().Or<T2>()` |
| `new PredicateBuilder<T>().HandleResult(…)` | `CanRetryResult` of `RetryTaskFactory<T>` |
| `HandleInner<T>()` | `RetryPolicies.ForInner<T>()` |
| `HttpClientResiliencePredicates.IsTransient` | `RetryPolicies.TransientHttp` for exceptions, and `RetryPolicies.IsTransientHttpResponse` for results |
| `AddFallback` | The `Fallback` property of `RetryTaskFactory<T>`, with a `Fallback<T>` |
| `AddCircuitBreaker` | The `CircuitBreaker` property, with one shared `CircuitBreaker` |
| `FailureRatio`, `MinimumThroughput`, `SamplingDuration`, `BreakDuration` | The constructor parameters `failureRate`, `minimumCalls`, `samplingWindow`, `openDuration` of `CircuitBreaker` |
| `BrokenCircuitException` | `CircuitRejectedException` |
| `IsolatedCircuitException` | `CircuitIsolatedException`, which derives from `CircuitRejectedException` |
| `CircuitBreakerManualControl` | `CircuitBreaker.Isolate()` and `CircuitBreaker.Close()` |
| `AddHedging` | A `HedgedTaskFactory` or `HedgedTaskFactory<T>` |
| `MaxHedgedAttempts`, `Delay` (hedging) | The constructor parameters `extraAttempts` and `stagger` of `HedgedTaskFactory` |
| `ActionGenerator` (hedging) | `OperationForAttempt` of `HedgedTaskFactory<T>` |
| `OnRetry`, `AttemptNumber` starts at 0 | `OnRetry`, `AttemptNumber` starts at 1 |
| `AddStandardResilienceHandler`, `AddResilienceHandler` | `AddRetryHandler(factory)` from the package `Corvus.Retry.Extensions.Http`. See [HttpClient](#httpclient). |
| Polly 7 `Policy.Handle<T>().OrResult<TResult>(…).WaitAndRetryAsync(…)` | `RetryTaskFactory<TResult>` with a strategy, a policy and `CanRetryResult`. See [Example 2](#example-2-polly-7-and-an-httpclient-marainusernotifications-airshipclient). |
| `TimeProvider` | `TimeProvider` |

### Same word, same meaning

These names are general terms. Polly and Corvus.Retry both use them, with the same meaning:

- `CircuitState`, and its four values `Closed`, `Open`, `HalfOpen` and `Isolated`
- `Isolate` and `Close`
- `OnRetry` and `OnFallback`
- `AttemptNumber`. The meaning is the same, but the first value is different: see [Primary differences](#primary-differences).
- `RetryAfter`
- `ContinueOnCapturedContext`
- `TimeProvider`

## Primary differences

- **Settings, not a builder.** Polly uses a builder. Corvus.Retry uses a factory with `init` properties and constructor parameters. Each `init` accessor examines its value when you set it. To make a changed copy of a factory, use the copy constructor: `new RetryTaskFactory(factory) { TotalTimeout = TimeSpan.FromSeconds(30) }`.
- **A fixed order of parts.** Polly adds the parts in the sequence of the builder calls. Corvus.Retry always uses one sequence: fallback, total timeout, retry or hedging, circuit breaker, attempt timeout, operation. See [Order of the parts](../README.md#order-of-the-parts).
- **Attempt numbers start at 1.** In Polly, `AttemptNumber` in `OnRetry` starts at 0. In Corvus.Retry, `AttemptNumber` is the number of the attempt that failed, and starts at 1. `NextAttemptNumber` gives the attempt that starts after the delay.
- **Hedging is a different type.** One factory cannot hedge and retry. To retry a hedged call, run a `HedgedTaskFactory` in the operation of a `RetryTaskFactory`.
- **The fallback applies only to the outcome of the attempts.** An exception from a callback, a strategy or a policy of the factory, for example from `OnRetry`, stops the call. The fallback does not give a value for it.
- **`Run` is Task-first.** `Run` takes and returns a `Task`. A Polly `ExecuteAsync` lambda that returns a `Task` does not need `new ValueTask<T>(…)`. For a `ValueTask`, use `RunValueTask`.
- **`Run` is not `Task.Run`.** `Run` runs the first attempt on the caller's thread until its first `await`, as Polly does. It does not queue the operation to the thread pool.
- **Rejected results are disposed.** When `CanRetryResult` rejects a result that implements `IDisposable`, for example an `HttpResponseMessage`, the factory disposes it before the next attempt. Polly does not.

## Synchronous calls

Polly has a synchronous `Execute`. Corvus.Retry has two synchronous forms:

- `Retriable.Retry` runs the operation on the caller's thread, waits for the delays, and throws the exception of the last attempt. This is the same as Polly's `Execute`.
- `StartNew` of a factory returns a `Task`, and runs the operation on a scheduler. If the retries stop, the task faults with an `AggregateException` of all the failures.

To replace `Execute`, use `Retriable.Retry`. Give it the same strategy and policy as the factory. Keep the strategy and the policy in fields: they have no state for a call, thus all calls can share them.

## Worked example: a total timeout, a retry and an attempt timeout

This Polly pipeline has a total timeout, a retry and an attempt timeout:

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

This is the equivalent Corvus.Retry factory:

```csharp
// Corvus.Retry 3.0
RetryTaskFactory factory = new(
    RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    RetryPolicies.For<HttpRequestException>().Or<RetryTimeoutException>())
{
    TotalTimeout = TimeSpan.FromSeconds(30),
    AttemptTimeout = TimeSpan.FromSeconds(5),
    OnRetry = args =>
    {
        Console.WriteLine($"Attempt {args.AttemptNumber} failed.");
        return ValueTask.CompletedTask;
    },
};

string body = await factory.Run(ct => httpClient.GetStringAsync(uri, ct), cancellationToken);
```

The order of `TotalTimeout` and `AttemptTimeout` in the initialiser has no effect. The total timeout is always outside the retries, and the attempt timeout is always inside them.

## Example 1: Polly 8 and file access (Rlm.Cli `SessionStore`)

`SessionStore` saves session files. It retries an `IOException` 3 times with exponential delays of 100 ms, 200 ms and 400 ms. It uses one static pipeline for asynchronous and synchronous calls:

```csharp
// Polly 8
private static readonly ResiliencePipeline RetryPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = MaxRetries,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(100),
        ShouldHandle = new PredicateBuilder().Handle<IOException>()
    })
    .Build();

// LoadAsync
return await RetryPipeline.ExecuteAsync(async ct =>
{
    IFile file = fileSystem.GetFile(path);
    string json = await file.ReadAllTextAsync();
    return JsonSerializer.Deserialize(json, RlmJsonContext.Default.RlmSession) ?? new RlmSession();
}, cancellationToken);

// SaveAsync
await RetryPipeline.ExecuteAsync(async _ =>
{
    IFile file = fileSystem.GetFile(path);
    await file.WriteAllTextAsync(json);
}, cancellationToken);

// Delete and DeleteAll
RetryPipeline.Execute(() => fileSystem.File.Delete(path));
RetryPipeline.Execute(() => file.Delete());
```

This is the Corvus.Retry version. The strategy and the policy are fields, because the synchronous calls use them too:

```csharp
// Corvus.Retry 3.0
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

// Delays of 100 ms, 200 ms and 400 ms, as in Polly. Polly does not use jitter unless UseJitter is true.
private static readonly IRetryStrategy RetryStrategy =
    RetryStrategies.Exponential(retries: MaxRetries, baseDelay: TimeSpan.FromMilliseconds(100));

private static readonly IRetryPolicy RetryPolicy = RetryPolicies.For<IOException>();

private static readonly RetryTaskFactory Retries = new(RetryStrategy, RetryPolicy);

// LoadAsync
return await Retries.Run(async ct =>
{
    IFile file = fileSystem.GetFile(path);
    string json = await file.ReadAllTextAsync();
    return JsonSerializer.Deserialize(json, RlmJsonContext.Default.RlmSession) ?? new RlmSession();
}, cancellationToken);

// SaveAsync
await Retries.Run(async _ =>
{
    IFile file = fileSystem.GetFile(path);
    await file.WriteAllTextAsync(json);
}, cancellationToken);

// Delete and DeleteAll
Retriable.Retry(() => fileSystem.File.Delete(path), CancellationToken.None, RetryStrategy, RetryPolicy);
Retriable.Retry(() => file.Delete(), CancellationToken.None, RetryStrategy, RetryPolicy);
```

These points apply:

- The asynchronous calls change only the name `ExecuteAsync` to `Run`. The lambdas return a `Task`, thus `Run` takes them as they are.
- `LoadAsync` catches a `JsonException`. The policy refuses a `JsonException`, thus `Run` throws it at once, without a retry. `Run` throws the exception itself, not an `AggregateException`. Thus, the `catch (JsonException)` block continues to operate.
- The synchronous `Execute` calls become `Retriable.Retry`, not `StartNew`. `Delete` and `DeleteAll` are synchronous methods. `Retriable.Retry` waits on the caller's thread and throws the exception of the last attempt, as `Execute` did. `StartNew` would return a `Task` that the method must wait for, and its failures would be in an `AggregateException`.
- The token of `LoadAsync` and `SaveAsync` now also stops a delay between attempts.

## Example 2: Polly 7 and an HttpClient (Marain.UserNotifications `AirshipClient`)

`AirshipClient` sends a push notification with a POST request. It retries an `HttpRequestException`, and the statuses 408, 500, 502, 503 and 504, 3 times. The delays are 2, 4 and 8 seconds:

```csharp
// Polly 7
this.retryPolicy = Policy
    .Handle<HttpRequestException>()
    .OrResult<HttpResponseMessage>(r => this.httpStatusCodesWorthRetrying.Contains(r.StatusCode))
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

var request = new HttpRequestMessage()
{
    RequestUri = new Uri($"{BaseUrl}/api/push"),
    Method = HttpMethod.Post,
    Content = new StringContent(requestContent),
};

HttpResponseMessage? result = await this.retryPolicy.ExecuteAsync(
    async () => await this.Client.SendAsync(request).ConfigureAwait(false));
```

> **Note:** The Polly version sends the same `HttpRequestMessage` again on each retry. `HttpClient` does not send a request message two times. The second `SendAsync` throws an `InvalidOperationException`, and the policy does not retry it. Thus, the Polly version never makes a successful retry. Both Corvus.Retry versions below correct this.

### Option A: a factory around the call

Make the request in the operation, so that each attempt sends a new request:

```csharp
// Corvus.Retry 3.0
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

private static readonly HttpStatusCode[] HttpStatusCodesWorthRetrying =
[
    HttpStatusCode.RequestTimeout,      // 408
    HttpStatusCode.InternalServerError, // 500
    HttpStatusCode.BadGateway,          // 502
    HttpStatusCode.ServiceUnavailable,  // 503
    HttpStatusCode.GatewayTimeout,      // 504
];

// Delays of 2, 4 and 8 seconds, as Math.Pow(2, retryAttempt) gave.
private readonly RetryTaskFactory<HttpResponseMessage> retries = new(
    RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(2)),
    RetryPolicies.For<HttpRequestException>())
{
    CanRetryResult = response => HttpStatusCodesWorthRetrying.Contains(response.StatusCode),
};

HttpResponseMessage result = await this.retries.Run(ct =>
{
    var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/push")
    {
        Content = new StringContent(requestContent),
    };

    return this.Client.SendAsync(request, ct);
});
```

These points apply:

- The status array is `static`, because a field initialiser cannot use an instance field.
- When no retries remain, the factory returns the last response, as Polly did. The factory disposes each rejected response before the next attempt.
- `ConfigureAwait(false)` is not necessary. `ContinueOnCapturedContext` is `false` by default.
- To retry 429 and every 5xx status, use `CanRetryResult = RetryPolicies.IsTransientHttpResponse` and the policy `RetryPolicies.TransientHttp`.

### Option B: a `RetryHandler` in the HttpClient

`AirshipClient` receives its `HttpClient` from the caller. Thus, the caller can put a `RetryHandler` in the client, and `AirshipClient` sends its request with no retry code. The handler sends the same `HttpRequestMessage` for each attempt, below `HttpClient`, thus the reused request is not a problem here. The default `CanRetryRequest` does not retry a POST request. `StringContent` can be sent again, thus set `CanRetryRequest = RetryHandler.CanReplayContent`:

```csharp
// Where the application makes the HttpClient for AirshipClient
using Corvus.Retry.Http;

HttpClient client = new(new RetryHandler(retries, new SocketsHttpHandler())
{
    CanRetryRequest = RetryHandler.CanReplayContent,
});

// With IHttpClientFactory and the package Corvus.Retry.Extensions.Http
services.AddHttpClient("airship").AddRetryHandler(retries, RetryHandler.CanReplayContent);

// In AirshipClient: no retry code.
HttpResponseMessage result = await this.Client.SendAsync(request);
```

Here `retries` is the `RetryTaskFactory<HttpResponseMessage>` of option A. Make it one time, and share it.

Do this only if the Airship API can receive the same push request two times without harm. A retry after a timeout can send a notification that the server already accepted.

`SendAsync` buffers the body of the response after the handler returns. With option B, a failure during that download is not retried. With option A, the download is inside `Run`, thus the factory retries it.

## HttpClient

`AddStandardResilienceHandler` and `AddResilienceHandler` from `Microsoft.Extensions.Http.Resilience` put a Polly pipeline in an `HttpClient`. In Corvus.Retry, `RetryHandler` puts a `RetryTaskFactory<HttpResponseMessage>` in an `HttpClient`. With `IHttpClientFactory`, use `AddRetryHandler(factory)` from the package `Corvus.Retry.Extensions.Http`:

```csharp
RetryTaskFactory<HttpResponseMessage> retries = new(
    RetryStrategies.RetryAfter(
        RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(2), useJitter: true)),
    RetryPolicies.TransientHttp)
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(10),
    TotalTimeout = TimeSpan.FromSeconds(30),
    CircuitBreaker = new CircuitBreaker(),
};

services.AddHttpClient<CatalogueClient>().AddRetryHandler(retries);
```

Make the factory one time. Each handler chain that `IHttpClientFactory` makes gets a new `RetryHandler`, and all of them share the factory and its circuit breaker.

The values in this example are close to the defaults of `AddStandardResilienceHandler`. Compare them with the defaults of the version of `Microsoft.Extensions.Http.Resilience` that you use. The defaults of `new CircuitBreaker()` are a failure rate of 0.1, 100 calls, a window of 30 seconds and an open duration of 5 seconds.

These differences apply:

- The standard handler also has a rate limiter. Corvus.Retry has no rate limiter.
- By default, `RetryHandler` retries only a request with a safe method (GET, HEAD, PUT, DELETE, OPTIONS or TRACE) and content that it can send again. To retry other requests, use `AddRetryHandler(retries, RetryHandler.CanReplayContent)`, or set `CanRetryRequest`. A request that the handler cannot retry still gets the timeouts, the circuit breaker, `CanRetryResult` and the fallback, for one attempt.
- `RetryHandler` does not hedge. To hedge, put the call in `Run` of a `HedgedTaskFactory<T>`.
- The synchronous `HttpClient.Send` throws a `NotSupportedException`.
- The handler returns when the headers arrive. A failure while `HttpClient` downloads a buffered body is not retried, and `AttemptTimeout` does not include it.
- The attempt and total timeouts of the factory give a `RetryTimeoutException`. Polly gives a `TimeoutRejectedException`.
- `HttpClient.Timeout` is outside the handler. It gives a `TaskCanceledException`, and the fallback does not apply to it. Make `TotalTimeout` shorter than `HttpClient.Timeout`.

The [README](../README.md#retryhandler) gives all the rules of `RetryHandler`.
