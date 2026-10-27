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
// stops at the next failure instead of using all of its tries, and the exception from that try is rethrown.
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
// There are several strategies provided out-of-the-box: `Count`, `DoNotRetry`, `Linear`, `Incremental`, and `Backoff`.
//
// #### `Count`
//
// We've seen this retry strategy in the `IRetryPolicy` examples earlier.
//
// This simply specifies the maximum number of times the method can be tried. `Retriable` operations default to a
// `new Count(10)` strategy; this means the method can be tried a maximum of ten times: once for the initial execution
// and nine retries.
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
// For example, `new Incremental(maxTries: 5, intialDelay: TimeSpan.FromSeconds(1), step: TimeSpan.FromSeconds(1))`
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
    Incremental incrementalStrategy = new(maxTries: 5, intialDelay: TimeSpan.FromSeconds(1), step: TimeSpan.FromSeconds(1));
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
// `MinBackoff` and `MaxBackoff` are available as settable properties on a `Backoff` object. If we use the same example
// as above but set the `MaxBackoff` to 5 seconds, we will see that once the number of retries reaches a number such
// that delay > 5 seconds, the delay between each retry will be restricted to 5 seconds.
Console.WriteLine();
Console.WriteLine("## Backoff strategy - MaxBackoff");
await RunExampleAsync(async () =>
{
    MockHttpService mockHttpService = new();
    Clock clock = new();
    Backoff backoffStrategy = new(maxTries: 5, deltaBackoff: TimeSpan.FromSeconds(1));
    backoffStrategy.MaxBackoff = TimeSpan.FromSeconds(5);
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
    Backoff backoffStrategy = new(maxTries: 4, deltaBackoff: TimeSpan.FromSeconds(1));
    backoffStrategy.MinBackoff = TimeSpan.FromSeconds(2);
    await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: backoffStrategy, policy: new RetryOnTooManyRequestsPolicy());
});

// #### Implementing a custom strategy
//
// It is slightly more complex to implement a custom retry strategy than it was to implement a retry policy. Although
// you can directly implement the `IRetryStrategy` interface, it is usually simpler to derive from the abstract
// `RetryStrategy` class.
//
// In that case, you need to override the `TimeSpan PrepareToRetry(Exception lastException)` method. You can examine
// the exception and use your internal state to determine two things:
//
// 1) Should we record this exception, or filter it out? Typically, we will want to record the exception, and, if so,
//    we call `this.AddException(lastException)`. You could also choose to create a new exception (with the original as
//    an inner exception) which decorates it with more information, and record that.
//
// 2) For how long should we delay before retrying? We return a `TimeSpan` from the method to determine this.
//
// So, for example, `Count` implements the method like this:
//
//     public override TimeSpan PrepareToRetry(Exception lastException)
//     {
//       if (lastException is null)
//       {
//         throw new ArgumentNullException(nameof(lastException));
//       }
//
//       this.AddException(lastException);
//       this.tryCount++;
//       return TimeSpan.Zero;
//     }
//
// It updates its internal state to keep a count of the number of retries, adds the exception to the list of
// exceptions we have seen, and tells the strategy not to delay before retrying.
//
// We also override the `bool CanRetry { get; }` property to determine whether this strategy still allows us to retry.
//
// The `Count` strategy, for example, simply uses its internal state to determine whether to continue.
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

// ##### Example - wrapping the caught exception in a new exception with additional information
//
// As mentioned, when overriding the `PrepareToRetry()` method, you could choose to wrap the detected exception in a
// new exception to record additional information.
//
// For example, you could wrap the detected exception in an exception with a property that records the `tryCount`
// value. I.e. "Which try caused this particular exception to be thrown?". You could do something similar to record
// how long we've been retrying when the exception was thrown, or anything else you'd like to record.
//
// `RandomDelayWithWrappedExceptions` (defined at the end of this file) does this with `MyWrapperException`. After the
// retry gives up, we print the exceptions the strategy recorded.
Console.WriteLine();
Console.WriteLine("## Custom strategy - RandomDelayWithWrappedExceptions");
{
    RandomDelayWithWrappedExceptions randomDelayStrategy = new(maxTries: 4, lowerLimit: 1, upperLimit: 5);
    await RunExampleAsync(async () =>
    {
        MockHttpService mockHttpService = new();
        Clock clock = new();
        await Retriable.RetryAsync(() => mockHttpService.MakeLotsOfRequestsAsync(clock), CancellationToken.None, strategy: randomDelayStrategy, policy: new RetryOnTooManyRequestsPolicy());
    });

    foreach (Exception recorded in randomDelayStrategy.Exception.InnerExceptions)
    {
        if (recorded is MyWrapperException wrapped)
        {
            Console.WriteLine($"Try {wrapped.TryCount}: {wrapped.InnerException?.Message}");
        }
    }
}

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
// (exclusive) before each retry.
public class RandomDelay : RetryStrategy
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
        if (maxTries <= 0)
        {
            throw new ArgumentException("Max tries must be > 0", nameof(maxTries));
        }

        this.maxTries = maxTries;
        this.lowerLimit = lowerLimit;
        this.upperLimit = upperLimit;
    }

    public override bool CanRetry => this.TryCount < this.maxTries;

    protected int TryCount { get; private set; }

    public override TimeSpan PrepareToRetry(Exception lastException)
    {
        if (lastException is null)
        {
            throw new ArgumentNullException(nameof(lastException));
        }

        this.RecordException(lastException);

        this.TryCount += 1;

        if (this.CanRetry)
        {
            return TimeSpan.FromSeconds(Random.Shared.Next(this.lowerLimit, this.upperLimit));
        }

        return TimeSpan.Zero;
    }

    // Records the exception. Override this to decorate the exception with more information.
    protected virtual void RecordException(Exception lastException)
    {
        this.AddException(lastException);
    }
}

// The same random delay strategy, but it wraps each exception in a MyWrapperException that records the try which
// caused it.
public class RandomDelayWithWrappedExceptions : RandomDelay
{
    public RandomDelayWithWrappedExceptions(int maxTries, int lowerLimit, int upperLimit)
        : base(maxTries, lowerLimit, upperLimit)
    {
    }

    protected override void RecordException(Exception lastException)
    {
        this.AddException(new MyWrapperException(lastException, this.TryCount + 1));
    }
}

// Wraps an exception and records the try which caused it.
public class MyWrapperException : Exception
{
    public MyWrapperException(Exception innerException, int tryCount)
        : base($"Try {tryCount} failed: {innerException.Message}", innerException)
    {
        this.TryCount = tryCount;
    }

    public int TryCount { get; }
}
