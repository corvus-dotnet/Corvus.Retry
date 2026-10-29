// <copyright file="StrategyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of each built-in strategy deciding on its delays, as a retry loop drives it through a run of failures.
/// </summary>
/// <remarks>
/// Strategies hold no state for a call, so each benchmark uses one strategy created once, and asks it for the delay
/// after each of <see cref="Failures"/> failed attempts, as a retry loop does, until it returns <see langword="null"/>.
/// No benchmark waits for the delays it calculates.
/// </remarks>
[MemoryDiagnoser]
public class StrategyBenchmarks
{
    private const int MaxTries = 10;

    private static readonly InvalidOperationException Failure = new("Transient failure");

    private static readonly Count CountStrategy = new(MaxTries);
    private static readonly DoNotRetry DoNotRetryStrategy = new();
    private static readonly Linear LinearStrategy = new(TimeSpan.FromSeconds(1), MaxTries);
    private static readonly Linear LinearWithJitterStrategy = new(TimeSpan.FromSeconds(1), MaxTries, TimeSpan.FromSeconds(5), useJitter: true);
    private static readonly Incremental IncrementalStrategy = new(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    private static readonly Incremental IncrementalWithJitterStrategy = new(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), useJitter: true);
    private static readonly Exponential ExponentialStrategy = new(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    private static readonly Exponential ExponentialWithJitterStrategy = new(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), useJitter: true);
    private static readonly Backoff BackoffStrategy = new(MaxTries, TimeSpan.FromSeconds(1));

    // Narrow the default bounds, as a caller tuning the strategy would.
    private static readonly Backoff BackoffWithBoundsStrategy = new(MaxTries, TimeSpan.FromMilliseconds(100))
    {
        MinBackoff = TimeSpan.FromMilliseconds(900),
        MaxBackoff = TimeSpan.FromSeconds(15),
    };

    private static readonly IRetryStrategy ConstantFactoryStrategy = RetryStrategies.Constant(MaxTries - 1, TimeSpan.FromSeconds(1));
    private static readonly IRetryStrategy LinearFactoryStrategy = RetryStrategies.Linear(MaxTries - 1, TimeSpan.FromSeconds(1));
    private static readonly IRetryStrategy ExponentialFactoryStrategy = RetryStrategies.Exponential(MaxTries - 1, TimeSpan.FromSeconds(1));
    private static readonly IRetryStrategy FromDelegateStrategy = RetryStrategies.FromDelegate(
        static attempt => attempt.AttemptNumber < MaxTries ? TimeSpan.FromSeconds(1) : null);

    private static readonly IRetryStrategy RetryAfterStrategy = RetryStrategies.RetryAfter(ConstantFactoryStrategy);

    // Pre-built responses, so that the benchmarks measure only the strategy reading the header.
    private static readonly HttpResponseMessage ResponseWithDelta = CreateResponse(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));
    private static readonly HttpResponseMessage ResponseWithDate = CreateResponse(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddYears(1)));
    private static readonly HttpResponseMessage ResponseWithoutHeader = new(HttpStatusCode.ServiceUnavailable);

    /// <summary>
    /// Gets or sets the number of failed attempts reported to each strategy.
    /// </summary>
    [Params(1, 9)]
    public int Failures { get; set; }

    [Benchmark(Baseline = true)]
    public TimeSpan Count() => this.Drive(CountStrategy, null);

    [Benchmark]
    public TimeSpan DoNotRetry() => this.Drive(DoNotRetryStrategy, null);

    [Benchmark]
    public TimeSpan Linear() => this.Drive(LinearStrategy, null);

    [Benchmark]
    public TimeSpan Linear_WithJitter() => this.Drive(LinearWithJitterStrategy, null);

    [Benchmark]
    public TimeSpan Incremental() => this.Drive(IncrementalStrategy, null);

    [Benchmark]
    public TimeSpan Incremental_WithJitter() => this.Drive(IncrementalWithJitterStrategy, null);

    [Benchmark]
    public TimeSpan Exponential() => this.Drive(ExponentialStrategy, null);

    [Benchmark]
    public TimeSpan Exponential_WithJitter() => this.Drive(ExponentialWithJitterStrategy, null);

    [Benchmark]
    public TimeSpan Backoff() => this.Drive(BackoffStrategy, null);

    [Benchmark]
    public TimeSpan Backoff_WithBounds() => this.Drive(BackoffWithBoundsStrategy, null);

    [Benchmark]
    public TimeSpan RetryStrategies_Default() => this.Drive(RetryStrategies.Default, null);

    [Benchmark]
    public TimeSpan RetryStrategies_Constant() => this.Drive(ConstantFactoryStrategy, null);

    [Benchmark]
    public TimeSpan RetryStrategies_Linear() => this.Drive(LinearFactoryStrategy, null);

    [Benchmark]
    public TimeSpan RetryStrategies_Exponential() => this.Drive(ExponentialFactoryStrategy, null);

    [Benchmark]
    public TimeSpan FromDelegate() => this.Drive(FromDelegateStrategy, null);

    [Benchmark]
    public TimeSpan RetryAfter_WithDeltaHeader() => this.Drive(RetryAfterStrategy, ResponseWithDelta);

    [Benchmark]
    public TimeSpan RetryAfter_WithDateHeader() => this.Drive(RetryAfterStrategy, ResponseWithDate);

    [Benchmark]
    public TimeSpan RetryAfter_WithoutHeader() => this.Drive(RetryAfterStrategy, ResponseWithoutHeader);

    private static HttpResponseMessage CreateResponse(RetryConditionHeaderValue retryAfter)
    {
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    private TimeSpan Drive(IRetryStrategy strategy, HttpResponseMessage? result)
    {
        // A result causes the retry when there is one; otherwise an exception does.
        Exception? exception = result is null ? Failure : null;
        TimeSpan total = TimeSpan.Zero;
        TimeSpan previousDelay = TimeSpan.Zero;
        for (int attempt = 1; attempt <= this.Failures; attempt++)
        {
            TimeSpan? delay = strategy.GetRetryDelay(new RetryAttempt(attempt, exception, result, previousDelay));
            if (delay is not TimeSpan next)
            {
                break;
            }

            total += next;
            previousDelay = next;
        }

        return total;
    }
}
