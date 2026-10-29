#:project ../../Solutions/Corvus.Retry/Corvus.Retry.csproj

// Corvus.Retry - RetryPipeline sample
// Run from the repository root:  dotnet run Documentation/Examples/RetryPipelineSample.cs
//
// This sample does not use the network. The operations are fakes in this process: they wait with Task.Delay, and
// fail when the sample tells them to fail. Each example prints the attempts and the outcome. The sample takes
// approximately 20 seconds.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

// ## The two reference pipelines
//
// A pipeline holds all the retry settings in one immutable object. Make one pipeline, keep it in a field, and use it
// for many calls. These two pipelines use options only.
//
// The first pipeline retries 3 times with an exponential delay and jitter. Each attempt has a time limit of 5 seconds.
RetryPipeline exponentialPipeline = new(new RetryPipelineOptions
{
    Strategy = RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200), useJitter: true),
    AttemptTimeout = TimeSpan.FromSeconds(5),
    OnRetry = PrintRetry,
});

// The second pipeline retries 5 times with a constant delay of 1 second. Each attempt has a time limit of 10 seconds,
// and the full call has a time limit of 30 seconds.
RetryPipeline constantPipeline = new(new RetryPipelineOptions
{
    TotalTimeout = TimeSpan.FromSeconds(30),
    Strategy = RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromSeconds(1)),
    AttemptTimeout = TimeSpan.FromSeconds(10),
    OnRetry = PrintRetry,
});

Console.WriteLine("## Exponential pipeline - two failures, then a success");
{
    FlakyService service = new(failures: 2);
    string value = await exponentialPipeline.ExecuteAsync(ct => service.GetAsync(ct));
    Console.WriteLine($"Result: {value}");
}

Console.WriteLine();
Console.WriteLine("## Constant pipeline - one failure, then a success");
{
    FlakyService service = new(failures: 1);
    string value = await constantPipeline.ExecuteAsync(ct => service.GetAsync(ct));
    Console.WriteLine($"Result: {value}");
}

// ## A timeout
//
// The attempt timeout cancels the token of a slow attempt. The attempt then fails with a RetryTimeoutException, and
// the policy decides if the pipeline retries it. Here, each attempt waits until its token is cancelled, so all the
// attempts time out, and the call throws the RetryTimeoutException of the last attempt.
Console.WriteLine();
Console.WriteLine("## Attempt timeout");
{
    RetryPipeline pipeline = new(new RetryPipelineOptions
    {
        Strategy = new Count(2),
        AttemptTimeout = TimeSpan.FromMilliseconds(500),
        OnRetry = PrintRetry,
    });

    try
    {
        await pipeline.ExecuteAsync(async ct =>
        {
            Console.WriteLine("The attempt starts, and waits until its token is cancelled");
            await Task.Delay(Timeout.Infinite, ct);
        });
    }
    catch (RetryTimeoutException ex)
    {
        Console.WriteLine($"RetryTimeoutException: the time limit of {ex.Timeout.TotalMilliseconds} ms expired");
    }
}

// ## A cancellation
//
// When the caller cancels the token, the pipeline stops immediately, also during a delay. It does not retry the
// cancellation, and the call throws an OperationCanceledException.
Console.WriteLine();
Console.WriteLine("## Cancellation by the caller");
{
    using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(1.5));
    FlakyService service = new(failures: 100);
    Stopwatch stopwatch = Stopwatch.StartNew();
    try
    {
        await constantPipeline.ExecuteAsync(ct => service.GetAsync(ct), cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.WriteLine($"OperationCanceledException after {stopwatch.Elapsed.TotalSeconds:F1} s and {service.Calls} attempts");
    }
}

// ## A result retry with Retry-After
//
// RetryPipeline<T> can retry a result. RetryPolicies.IsTransientHttpResponse rejects a status of 408, 429, or 500 or
// more. RetryStrategies.RetryAfter uses the Retry-After header of the response for the delay. The pipeline disposes
// each rejected response before the next attempt.
Console.WriteLine();
Console.WriteLine("## Result retry with Retry-After");
{
    RetryPipeline<HttpResponseMessage> pipeline = new(new RetryPipelineOptions<HttpResponseMessage>
    {
        Strategy = RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromSeconds(5))),
        Policy = RetryPolicies.TransientHttp,
        ShouldRetryResult = RetryPolicies.IsTransientHttpResponse,
        OnRetry = PrintRetry,
    });

    int calls = 0;
    using HttpResponseMessage response = await pipeline.ExecuteAsync(ct =>
    {
        calls++;
        if (calls == 1)
        {
            // The first response tells the caller to wait 1 second. The inner strategy would wait 5 seconds.
            HttpResponseMessage busy = new(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(busy);
        }

        return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    });

    Console.WriteLine($"Status: {(int)response.StatusCode} after {calls} attempts");
}

// ## A fallback
//
// A fallback gives a value when the pipeline fails. It is the outermost part of the pipeline, so it receives the
// exception after all the retries.
Console.WriteLine();
Console.WriteLine("## Fallback");
{
    RetryPipeline<string> pipeline = new(new RetryPipelineOptions<string>
    {
        Strategy = RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(200)),
        OnRetry = PrintRetry,
        Fallback = new FallbackOptions<string>
        {
            Action = args => ValueTask.FromResult("the cached value"),
            OnFallback = args =>
            {
                Console.WriteLine($"The fallback applies after {args.Exception?.GetType().Name}");
                return ValueTask.CompletedTask;
            },
        },
    });

    FlakyService service = new(failures: 100);
    string value = await pipeline.ExecuteAsync(ct => service.GetAsync(ct));
    Console.WriteLine($"Result: {value}");
}

// ## A circuit breaker
//
// The breaker opens when the ratio of failures is too high. An open circuit rejects each call immediately with a
// BrokenCircuitException. After the break, the breaker lets one test call through. If it succeeds, the circuit closes.
// Many pipelines can share one breaker.
Console.WriteLine();
Console.WriteLine("## Circuit breaker");
{
    CircuitBreaker breaker = new(new CircuitBreakerOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 4,
        BreakDuration = TimeSpan.FromSeconds(1),
        OnStateChanged = change =>
        {
            Console.WriteLine($"The circuit changed from {change.From} to {change.To}");
            return ValueTask.CompletedTask;
        },
    });

    // One attempt for each call, so that each call is one call to the breaker.
    RetryPipeline pipeline = new(new RetryPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker });
    FlakyService service = new(failures: 4);

    for (int call = 1; call <= 6; call++)
    {
        try
        {
            await pipeline.ExecuteAsync(ct => service.GetAsync(ct));
            Console.WriteLine($"Call {call} succeeded");
        }
        catch (BrokenCircuitException ex)
        {
            Console.WriteLine($"Call {call} was rejected. Retry after {ex.RetryAfter?.TotalSeconds:F1} s");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"Call {call} failed");
        }
    }

    Console.WriteLine("Wait until the break ends");
    await Task.Delay(TimeSpan.FromSeconds(1.2));
    await pipeline.ExecuteAsync(ct => service.GetAsync(ct));
    Console.WriteLine($"The test call succeeded. The circuit is {breaker.State}");

    breaker.Isolate();
    Console.WriteLine($"After Isolate(), the circuit is {breaker.State}");
    breaker.Close();
}

// ## Hedging
//
// Hedging starts a second attempt when the first attempt is slow. The first attempt that succeeds wins, and the
// pipeline cancels the other attempt. Here, the first attempt takes 2 seconds, and the hedged attempt uses a
// different, faster operation.
Console.WriteLine();
Console.WriteLine("## Hedging");
{
    RetryPipeline<string> pipeline = new(new RetryPipelineOptions<string>
    {
        Hedging = new HedgingOptions
        {
            MaxHedgedAttempts = 1,
            Delay = TimeSpan.FromMilliseconds(300),
            OnHedging = args =>
            {
                Console.WriteLine($"Attempt {args.AttemptNumber} starts");
                return ValueTask.CompletedTask;
            },
        },
        HedgingActionGenerator = attemptNumber => attemptNumber == 1 ? null : ct => SlowOrFastAsync("secondary", 100, ct),
    });

    Stopwatch stopwatch = Stopwatch.StartNew();
    string value = await pipeline.ExecuteAsync(ct => SlowOrFastAsync("primary", 2000, ct));
    Console.WriteLine($"Result: {value} after {stopwatch.Elapsed.TotalSeconds:F1} s");
}

// Prints the data of each retry. The pipeline awaits this callback before the delay.
static ValueTask PrintRetry(RetryEventArgs args)
{
    string cause = args.Result is HttpResponseMessage response
        ? $"the status {(int)response.StatusCode}"
        : args.LastException?.GetType().Name ?? $"the result {args.Result}";
    Console.WriteLine(
        $"Attempt {args.AttemptNumber} failed with {cause}. Attempt {args.NextAttemptNumber} starts after {args.DelayBeforeRetry.TotalMilliseconds:F0} ms");
    return ValueTask.CompletedTask;
}

// Waits for a time, and then returns the name of the endpoint. The token stops the wait.
static async ValueTask<string> SlowOrFastAsync(string endpoint, int milliseconds, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(milliseconds, cancellationToken);
        return $"the value from the {endpoint} endpoint";
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"The {endpoint} attempt was cancelled");
        throw;
    }
}

// ---- Types used above (must follow the top-level statements) ----

// A fake service that fails a given number of times, and then succeeds. Each call waits 100 ms.
public sealed class FlakyService(int failures)
{
    public int Calls { get; private set; }

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        this.Calls++;
        await Task.Delay(100, cancellationToken);
        if (this.Calls <= failures)
        {
            throw new InvalidOperationException($"Failure {this.Calls}");
        }

        return $"the value of call {this.Calls}";
    }
}
