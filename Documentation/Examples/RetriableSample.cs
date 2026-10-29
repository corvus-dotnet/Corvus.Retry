#:project ../../Solutions/Corvus.Retry/Corvus.Retry.csproj

// Corvus.Retry - Retriable sample
// Run from the repository root:  dotnet run Documentation/Examples/RetriableSample.cs
// The narrative from the original Polyglot notebook is kept as comments above each section.
//
// This sample pings endjin.com, so it needs network access. Several examples deliberately wait between
// retries, so it takes a while to complete.
//
// Many examples call an operation that always fails, so the retry eventually gives up and rethrows the last
// exception. The sample catches those exceptions with RunExampleAsync (defined at the end of the top-level
// statements) and prints them, so that every example runs and the app exits normally.

using System.Diagnostics;
using System.Net.NetworkInformation;
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

// `Retriable` is the most common usage pattern from the `Corvus.Retry` library. It wraps an existing method and
// allows you to re-execute the method if it throws an exception.
//
//     Retriable.Retry(() => DoSomethingThatMightFail());

// ### `Retriable.Retry` - basic example
//
// The following example uses `Retry` to wrap a method that pings an internet host.
Console.WriteLine("## Retriable.Retry - basic example");
{
    using Ping pingSender = new();
    Retriable.Retry(() => pingSender.Send("endjin.com"));
    Console.WriteLine("Ping sent");
}

// You can also return the values from the method. The following example uses the `PingReply` object returned by
// `Ping.Send()`:
Console.WriteLine();
Console.WriteLine("## Retriable.Retry - returning a value");
{
    using Ping pingSender = new();
    PingReply reply = Retriable.Retry(() => pingSender.Send("endjin.com"));

    // Print a property from the response
    Console.WriteLine($"Address:\t{reply.Address}\tStatus:\t{reply.Status}");
}

// ### `Retriable.RetryAsync` - example
//
// The `RetryAsync` method allows you to wrap an asynchronous method to re-execute it.
//
// The following example uses `Retriable` to wrap an asynchronous method that sends a ping to an internet host and
// receives a response.
Console.WriteLine();
Console.WriteLine("## Retriable.RetryAsync - example");
{
    using Ping pingSender = new();
    PingReply reply = await Retriable.RetryAsync(() => pingSender.SendPingAsync("endjin.com"));

    // Print a property from the response
    Console.WriteLine($"Address:\t{reply.Address}\tStatus:\t{reply.Status}");
}

// ## Cancellation token
//
// You can pass a cancellation token to `Retry` to signal cancellation of the retry process.
//
// ### Using a cancellation token - example
//
// If we ping a non existing host, the method will be retried multiple times and the exception will eventually bubble
// up (after the default 10 tries).
Console.WriteLine();
Console.WriteLine("## Pinging a host that does not exist (default 10 tries)");
await RunExampleAsync(async () =>
{
    using Ping pingSender = new();
    PingReply pingReply = await Retriable.RetryAsync(() => pingSender.SendPingAsync("this-internet-host-does-not-exist"));
});

// We can now call the same method and cancel the retry operation using a cancellation token. The retry operation
// starts no further attempt after the token is cancelled, and the call throws an OperationCanceledException.
Console.WriteLine();
Console.WriteLine("## Cancelling the retry operation with a cancellation token");
await RunExampleAsync(async () =>
{
    using CancellationTokenSource cancellationTokenSource = new();
    CancellationToken cancellationToken = cancellationTokenSource.Token;

    using Ping pingSender = new();
    Task<PingReply> task = Retriable.RetryAsync<PingReply>(() => pingSender.SendPingAsync("this-internet-host-does-not-exist"), cancellationToken);
    cancellationTokenSource.Cancel();
    await task;
});

// ### Giving the token to the operation, and a retry callback - example
//
// The overloads for `Func<CancellationToken, Task<T>>` and `Func<CancellationToken, Task>` give the token to the
// operation, so the operation can stop when the token is cancelled. The token also stops a delay immediately.
//
// The overloads that have all the parameters also take an optional `onRetry` callback. `Retriable` awaits the callback
// before each delay. `RetryEventArgs.AttemptNumber` is the number of the attempt that failed (the first attempt is 1),
// and `NextAttemptNumber` is the attempt that starts after the delay. The callback is not called for the last failure.
//
// Here the token is cancelled after 2.5 seconds, during the third delay, so the call throws an
// `OperationCanceledException`.
Console.WriteLine();
Console.WriteLine("## Token overload with an onRetry callback");
await RunExampleAsync(async () =>
{
    using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(2.5));
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(
        ct => mockHttpService.MakeLotsOfRequestsAsync(ct),
        cancellationTokenSource.Token,
        RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromSeconds(1)),
        new RetryOnTooManyRequestsPolicy(),
        continueOnCapturedContext: false,
        onRetry: args =>
        {
            Console.WriteLine($"Attempt {args.AttemptNumber} failed: {args.LastException?.Message}. Attempt {args.NextAttemptNumber} starts after {args.DelayBeforeRetry.TotalSeconds}s");
            return ValueTask.CompletedTask;
        });
});

// ## Policy and Strategy
//
// Two types help you to control when a failed operation is retried, and how that retry occurs: `IRetryPolicy` and
// `IRetryStrategy`. You can pass these as arguments to the parameters `strategy` and `policy` on the `Retry` and
// `RetryAsync` methods.

// ### `IRetryPolicy`
//
// The retry policy gives you the ability to determine whether the operation should be retried or not, based on the
// exception that has been thrown.
//
// This typically means you can distinguish between terminal exceptions (like bad input) and transient exceptions
// (like a network timeout).
//
// There are three built-in retry policies: `AnyExceptionPolicy`, `DoNotRetryPolicy`, and `AggregatePolicy`.
//
// #### `AnyExceptionPolicy`
//
// This will always retry on any exception, and is the default for `Retriable`.
//
// Note: the argument to the `strategy` parameter in the example below specifies that the function should be tried a
// maximum of 10 times (i.e. 9 retries). `IRetryStrategy` is covered later in this sample.
Console.WriteLine();
Console.WriteLine("## AnyExceptionPolicy");
{
    using Ping pingSender = new();
    PingReply reply = await Retriable.RetryAsync(() => pingSender.SendPingAsync("endjin.com"), CancellationToken.None, strategy: new Count(10), policy: new AnyExceptionPolicy());
    Console.WriteLine($"Status:\t{reply.Status}");
}

// #### `DoNotRetryPolicy`
//
// This will never retry, regardless of the exception. You use this to disable retry, without having to comment out
// your retry code.
//
// Typically, you would do this with some kind of conditional:
Console.WriteLine();
Console.WriteLine("## DoNotRetryPolicy");
{
    bool isDebuggingOrWhatever = true;

    IRetryPolicy policy = isDebuggingOrWhatever ? new DoNotRetryPolicy() : new AnyExceptionPolicy();

    using Ping pingSender = new();
    PingReply reply = await Retriable.RetryAsync(() => pingSender.SendPingAsync("endjin.com"), CancellationToken.None, strategy: new Count(10), policy: policy);
    Console.WriteLine($"Status:\t{reply.Status}");
}

// #### Implementing a custom policy
//
// To create your own custom policy implement the `IRetryPolicy` interface, and its
// `bool CanRetry(Exception exception)` method which allows you to define the behaviour for determining whether to
// retry based on the exception that was raised.
//
// ##### Example - basic
//
// Continuing with the `Ping.Send()` method, we can define a policy that will retry when a `PingException` exception
// is detected. (See `RetryOnPingExceptionPolicy` at the end of this file.)
Console.WriteLine();
Console.WriteLine("## Custom policy - RetryOnPingExceptionPolicy");
{
    using Ping pingSender = new();
    PingReply reply = await Retriable.RetryAsync(() => pingSender.SendPingAsync("endjin.com"), CancellationToken.None, strategy: new Count(10), policy: new RetryOnPingExceptionPolicy());
    Console.WriteLine($"Status:\t{reply.Status}");
}

// ##### Example - mock HTTP service
//
// Let's imagine we were consuming an HttpService which occasionally gives us a `429 - Too Many Requests` error in an
// `HttpServiceException`.
//
// We can implement a policy which will only retry if we receive this exception.
//
// Let's first create a mock HTTP service: `MockHttpService` and a mock HTTP service exception:
// `MockHttpServiceException` (both defined at the end of this file).
//
// `MockHttpService` has two methods: `MakeLotsOfRequestsAsync`, that always throws a `MockHttpServiceException` with a
// status code of `"429"`; and `MakeRequestThatWillTimeOutAsync`, that always throws a `MockHttpServiceException` with
// a status code of `"408"`. Both methods also write a message to the console containing their name so that we can see
// the number of times they get called.
//
// Now, implement a custom policy that will only retry when a `MockHttpServiceException` exception type is detected
// and the status code is `"429"` (see `RetryOnTooManyRequestsPolicy` at the end of this file).
//
// Now, create an instance of `MockHttpService`, and call its `MakeLotsOfRequestsAsync` method - wrapping it with
// `Retriable.RetryAsync` and passing it our `RetryOnTooManyRequestsPolicy` policy.
//
// For the strategy we pass in `new Count(10)`, which means the method will be tried a total of ten times maximum.
// Therefore we should expect to see "MakeLotsOfRequests() method called" printed to the console 10 times.
Console.WriteLine();
Console.WriteLine("## Custom policy - RetryOnTooManyRequestsPolicy with a 429 error");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Count(10), policy: new RetryOnTooManyRequestsPolicy());
});

// Now, instead of calling `MakeLotsOfRequestsAsync()`, let's call `MakeRequestThatWillTimeOutAsync()`, whilst keeping
// the policy the same. The condition in `RetryOnTooManyRequestsPolicy` will not pass, and therefore the method will
// not be retried; we should expect to see `MakeRequestThatWillTimeOut() method called` printed just once to the
// console for the initial method execution, and the `408 - Request timed out` exception thrown.
Console.WriteLine();
Console.WriteLine("## Custom policy - RetryOnTooManyRequestsPolicy with a 408 error");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeRequestThatWillTimeOutAsync(), CancellationToken.None, strategy: new Count(10), policy: new RetryOnTooManyRequestsPolicy());
});

// #### `AggregatePolicy`
//
// This gives you a means of combining together multiple policies.
//
// The `AggregatePolicy` only succeeds if *ALL* of its children succeed.
//
// An aggregate policy takes the following form:
//
//     var aggregatePolicy =
//       new AggregatePolicy
//       {
//         Policies =
//         {
//           new CustomPolicy1(),
//           new CustomPolicy2(),
//         },
//       };
//
// ##### Example - mock HTTP service
//
// In this example we create a policy that checks configuration to see if retry is disabled. You could have such
// configuration to allow you to disable retry whilst the application is running in production.
//
// First create a class encapsulating configuration which says whether retry is disabled or not (`RetryConfiguration`).
//
// Next, create a custom policy which takes a `RetryConfiguration` to construct, and uses its `IsRetryDisabled`
// property to determine whether to retry or not (`RetryOnRetryEnabledPolicy`).
//
// Now, define an aggregate policy that combines `RetryOnRetryEnabledPolicy` & `RetryOnTooManyRequestsPolicy`, and try
// it with the `MockHttpService.MakeLotsOfRequestsAsync()` method.
//
// The default value of the `IsRetryDisabled` property on `RetryConfiguration` is `false`, therefore the two conditions
// in `aggregatePolicy` will pass, and the method will be retried every time.
Console.WriteLine();
Console.WriteLine("## AggregatePolicy - retry enabled");
await RunExampleAsync(async () =>
{
    AggregatePolicy aggregatePolicy = new()
    {
        Policies =
        {
            new RetryOnTooManyRequestsPolicy(),
            new RetryOnRetryEnabledPolicy(new RetryConfiguration()),
        },
    };

    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Count(10), policy: aggregatePolicy);
});

// If we set the `IsRetryDisabled` property on `RetryConfiguration` to `true`, the condition in
// `RetryOnRetryEnabledPolicy` will not pass, and therefore `aggregatePolicy` will not trigger a retry.
Console.WriteLine();
Console.WriteLine("## AggregatePolicy - retry disabled");
await RunExampleAsync(async () =>
{
    RetryConfiguration retryConfiguration = new();
    RetryOnRetryEnabledPolicy retryOnRetryEnabledPolicy = new(retryConfiguration);

    AggregatePolicy aggregatePolicy = new()
    {
        Policies =
        {
            new RetryOnTooManyRequestsPolicy(),
            retryOnRetryEnabledPolicy,
        },
    };

    // Change IsRetryDisabled to true
    retryConfiguration.IsRetryDisabled = true;

    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Count(10), policy: aggregatePolicy);
});

// If something similar were running in production, there may be a config change that prevents retries *whilst* a
// retry run is in progress. Let's simulate that by starting a retry run (using the `Linear` strategy such that the
// delay between each retry is 2 seconds) asynchronously, and change the `IsRetryDisabled` config property whilst it's
// running.
Console.WriteLine();
Console.WriteLine("## AggregatePolicy - retry disabled while retrying");
await RunExampleAsync(async () =>
{
    RetryConfiguration retryConfiguration = new();
    RetryOnRetryEnabledPolicy retryOnRetryEnabledPolicy = new(retryConfiguration);

    AggregatePolicy aggregatePolicy = new()
    {
        Policies =
        {
            new RetryOnTooManyRequestsPolicy(),
            retryOnRetryEnabledPolicy,
        },
    };

    MockHttpService mockHttpService = new();
    Task retryTask = Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Linear(TimeSpan.FromSeconds(2), 10), policy: aggregatePolicy);
    await Task.Delay(5000); // Wait some time before disabling retry
    retryConfiguration.IsRetryDisabled = true; // Disable retry via the config
    Console.WriteLine("Retry disabled via configuration");
    await retryTask;
});

// ### `IRetryStrategy`
//
// The `IRetryStrategy` controls the way in which the operation is retried. It controls both the _delay between each
// retry_ and the _number of times that it will be retried_.
//
// There are several strategies provided out-of-the-box: `Count`, `DoNotRetry`, `Linear`, `Incremental`, `Backoff` and
// `Exponential`, and the `RetryStrategies` factory methods (`Constant`, `Linear`, `Exponential`, `FromDelegate` and
// `RetryAfter`), which count retries rather than attempts. RetryPipelineSample.cs shows some of them.
//
// #### `Count`
//
// We've seen this retry strategy in the `IRetryPolicy` examples earlier.
//
// This simply specifies the maximum number of times the method can be tried. `Retriable` operations default to
// `RetryStrategies.Default`, a shared `Count(10)` strategy; this means the method can be tried a maximum of ten times:
// once for the initial execution and nine retries. A strategy holds no state for a call, so one instance can be shared
// by many calls.
//
// ##### Example
//
// Reusing the `MockHttpService` from earlier, if we call the `MakeLotsOfRequestsAsync()` method (which always throws a
// "429 - Too Many Requests" `MockHttpServiceException`) and supply a `RetryOnTooManyRequestsPolicy` for the policy,
// and `new Count(10)` for the strategy, we should expect to see the method call tried 10 times.
Console.WriteLine();
Console.WriteLine("## Count strategy");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Count(10), policy: new RetryOnTooManyRequestsPolicy());
});

// #### `DoNotRetry`
//
// This is the strategy equivalent of the `DoNotRetryPolicy`. It forces a retry to be abandoned, regardless of policy.
//
// ##### Example
//
// Using the same example as in the `Count` strategy above, the method will be called once and not retried.
Console.WriteLine();
Console.WriteLine("## DoNotRetry strategy");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new DoNotRetry(), policy: new RetryOnTooManyRequestsPolicy());
});

// #### `Linear`
//
// This tries a specified number of times (specified by `maxTries`), with a constant delay between each try (specified
// by `periodicity`).
//
// For example, `new Linear(TimeSpan.FromSeconds(1), 5)` will try up to 5 times. The initial try will be immediate,
// each retry will be delayed by 1s. So the first retry will be after 1s (wall clock time 1s), the second after
// another 1s (wall clock time 2s), the third after another 1s (wall clock time 3s).
Console.WriteLine();
Console.WriteLine("## Linear strategy");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(), CancellationToken.None, strategy: new Linear(periodicity: TimeSpan.FromSeconds(1), maxTries: 5), policy: new RetryOnTooManyRequestsPolicy());
});

// If we introduce a `Clock` object and add an overload for `MockHttpService.MakeLotsOfRequestsAsync()` that records
// the time elapsed for each retry since the initial execution, we can see how the `Linear` strategy retries over
// time.
Console.WriteLine();
Console.WriteLine("## Linear strategy - with timings");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: new Linear(periodicity: TimeSpan.FromSeconds(1), maxTries: 5), policy: new RetryOnTooManyRequestsPolicy());
});

// #### `Incremental`
//
// This tries a specified number of times, with an arithmetically increasing delay between each retry.
//
// For example, `new Incremental(maxTries: 5, initialDelay: TimeSpan.FromSeconds(1), step: TimeSpan.FromSeconds(1))`
// will try up to 5 times. The initial try will be immediate, the delay between the initial try and the first retry
// will be one second. Each time it will increase the delay by 1s. So the first retry will be after 1s (wall clock time
// 1s), the second after another 2s (wall clock time 3s), the third after another 3s (wall clock time 6s).
//
// This allows a slowly increasing delay.
//
// ##### Example
//
// Using the same example as with `Linear` above, we can see how the `Incremental` strategy retries over time.
Console.WriteLine();
Console.WriteLine("## Incremental strategy");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    Incremental incrementalStrategy = new(maxTries: 5, initialDelay: TimeSpan.FromSeconds(1), step: TimeSpan.FromSeconds(1));
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: incrementalStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// #### `Backoff`
//
// This tries a specified number of times, with a delay that increases geometrically between each retry.
//
// For example, `new Backoff(maxTries: 5, deltaBackoff: TimeSpan.FromSeconds(1))` will try up to 5 times. Each time it
// will increase the delay by a value calculated roughly like this: `2^n * (delta +/- a small random fudge)`, where `n`
// is the current number of retries.
//
// This allows a rapidly increasing delay, with a bit of random jitter added to avoid contention.
//
// You can also set a `MinBackoff` and `MaxBackoff` to limit the lower and upper bounds of the delay.
//
// ##### Example
Console.WriteLine();
Console.WriteLine("## Backoff strategy");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    Backoff backoffStrategy = new(maxTries: 4, deltaBackoff: TimeSpan.FromSeconds(1));
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: backoffStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// ##### Example - using `MinBackoff` and `MaxBackoff`
//
// You set `MinBackoff` and `MaxBackoff` with an object initializer when you create a `Backoff`. If we use the same example
// as above but set the `MaxBackoff` to 5 seconds, we will see that once the number of retries reaches a number such
// that delay > 5 seconds, the delay between each retry will be restricted to 5 seconds.
Console.WriteLine();
Console.WriteLine("## Backoff strategy - MaxBackoff");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    Backoff backoffStrategy = new(maxTries: 5, deltaBackoff: TimeSpan.FromSeconds(1)) { MaxBackoff = TimeSpan.FromSeconds(5) };
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: backoffStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// And, if we set the `MinBackoff` to be 2 seconds (the default is 1 second), we will see the time elapsed between
// each retry increase by 1 second on average.
Console.WriteLine();
Console.WriteLine("## Backoff strategy - MinBackoff");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    Backoff backoffStrategy = new(maxTries: 4, deltaBackoff: TimeSpan.FromSeconds(1)) { MinBackoff = TimeSpan.FromSeconds(2) };
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: backoffStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// #### Implementing a custom strategy
//
// To implement a custom retry strategy, implement the `IRetryStrategy` interface. It has one method:
// `TimeSpan? GetRetryDelay(in RetryAttempt attempt)`.
//
// The `RetryAttempt` describes the attempt that failed:
//
// - `AttemptNumber` is the number of the attempt that failed. The first attempt is 1.
// - `Exception` is the exception that the attempt threw.
// - `PreviousDelay` is the delay before the attempt that failed (zero for the first attempt).
//
// The method returns the delay before the next attempt, or `null` to stop retrying.
//
// A strategy must not hold state for a call. `Retriable` and `RetryTask` keep the attempt number and the previous
// delay for each call, and give them to the strategy. So one strategy instance can be shared, and many calls can use
// it at the same time.
//
// So, for example, `Count` implements the method like this:
//
//     public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
//     {
//         return attempt.AttemptNumber < this.maxTries ? TimeSpan.Zero : null;
//     }
//
// It compares the attempt number with the maximum number of tries, and tells the caller not to delay before retrying.
//
// ##### Example
//
// The `RandomDelay` strategy (defined at the end of this file) determines the delay by randomly selecting a number of
// seconds between user-provided upper and lower limits.
//
// Let's try it...
Console.WriteLine();
Console.WriteLine("## Custom strategy - RandomDelay");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    RandomDelay randomDelayStrategy = new(maxTries: 4, lowerLimit: 1, upperLimit: 5);
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: randomDelayStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// ##### Example - choosing the delay from the exception
//
// The strategy also receives the exception of the attempt that failed, so it can choose a different delay for a
// different failure. It must not keep the exception: the strategy does not hold state for a call.
//
// `DelayByStatusCode` (defined at the end of this file) waits longer after a "429 - Too Many Requests" failure than
// after other failures. `RunExampleAsync` prints the last exception when the retry gives up.
Console.WriteLine();
Console.WriteLine("## Custom strategy - DelayByStatusCode");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    DelayByStatusCode strategy = new(maxTries: 3);
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: strategy, policy: new RetryOnTooManyRequestsPolicy());
});

// Runs an example and prints the exception that the retry operation rethrows when it gives up.
static async Task RunExampleAsync(Func<Task> example)
{
    try
    {
        await example();
        Console.WriteLine("Completed successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Retry gave up. {ex.GetType().Name}: {ex.Message}");
    }
}

// ---- Types used above (must follow the top-level statements) ----

// Retries only when a `PingException` is detected.
public class RetryOnPingExceptionPolicy : IRetryPolicy
{
    public bool CanRetry(Exception exception)
    {
        return exception is PingException;
    }
}

// The exception thrown by the mock HTTP service.
public class MockHttpServiceException : Exception
{
    public MockHttpServiceException(string message, string statusCode)
        : base(message)
    {
        this.StatusCode = statusCode;
    }

    public string StatusCode { get; }
}

// A mock HTTP service whose methods always fail. Each method writes its name to the console so that you can count
// the number of tries.
public class MockHttpService
{
    public async Task MakeLotsOfRequestsAsync()
    {
        Console.WriteLine("MakeLotsOfRequests() method called");
        await Task.Delay(100);
        throw new MockHttpServiceException("429 - Too Many Requests", "429");
    }

    // This overload records the time elapsed for each retry since the initial execution.
    public async Task MakeLotsOfRequestsAsync(Clock clock)
    {
        Console.WriteLine("MakeLotsOfRequests() method called");

        if (clock.Count != 0)
        {
            Console.WriteLine($"Time elapsed: {clock.Stopwatch.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            clock.Stopwatch.Start();
        }

        clock.IncrementCounter();
        await Task.Delay(100);
        throw new MockHttpServiceException("429 - Too Many Requests", "429");
    }

    // This overload receives a cancellation token, so the token can stop the call.
    public async Task MakeLotsOfRequestsAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("MakeLotsOfRequests(CancellationToken) method called");
        await Task.Delay(100, cancellationToken);
        throw new MockHttpServiceException("429 - Too Many Requests", "429");
    }

    public async Task MakeRequestThatWillTimeOutAsync()
    {
        Console.WriteLine("MakeRequestThatWillTimeOut() method called");
        await Task.Delay(100);
        throw new MockHttpServiceException("408 - Request timed out", "408");
    }
}

// Retries only when the mock HTTP service reports "429 - Too Many Requests".
public class RetryOnTooManyRequestsPolicy : IRetryPolicy
{
    public bool CanRetry(Exception exception)
    {
        return exception is MockHttpServiceException httpException && httpException.StatusCode == "429";
    }
}

// Configuration which says whether retry is disabled or not.
public class RetryConfiguration
{
    public bool IsRetryDisabled { get; set; }
}

// Retries only while the configuration says that retry is enabled.
public class RetryOnRetryEnabledPolicy : IRetryPolicy
{
    private readonly RetryConfiguration retryConfiguration;

    public RetryOnRetryEnabledPolicy(RetryConfiguration retryConfiguration)
    {
        this.retryConfiguration = retryConfiguration;
    }

    public bool CanRetry(Exception exception)
    {
        return !this.retryConfiguration.IsRetryDisabled;
    }
}

// Counts the calls and measures the time since the initial execution.
public class Clock
{
    public int Count { get; private set; }

    public Stopwatch Stopwatch { get; } = new();

    public void IncrementCounter()
    {
        this.Count += 1;
    }
}

// A custom strategy that waits for a random number of seconds between lowerLimit (inclusive) and upperLimit
// (exclusive) before each retry. It holds no state for a call, so one instance can be shared.
public class RandomDelay : IRetryStrategy
{
    private readonly int maxTries;
    private readonly int lowerLimit;
    private readonly int upperLimit;

    public RandomDelay()
        : this(maxTries: 5, lowerLimit: 1, upperLimit: 5)
    {
    }

    public RandomDelay(int maxTries, int lowerLimit, int upperLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);

        this.maxTries = maxTries;
        this.lowerLimit = lowerLimit;
        this.upperLimit = upperLimit;
    }

    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= this.maxTries)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Random.Shared.Next(this.lowerLimit, this.upperLimit));
    }
}

// A custom strategy that waits 3 seconds after a "429 - Too Many Requests" failure, and 1 second after other failures.
public class DelayByStatusCode : IRetryStrategy
{
    private readonly int maxTries;

    public DelayByStatusCode(int maxTries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTries);

        this.maxTries = maxTries;
    }

    public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
    {
        if (attempt.AttemptNumber >= this.maxTries)
        {
            return null;
        }

        return attempt.Exception is MockHttpServiceException { StatusCode: "429" }
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(1);
    }
}
