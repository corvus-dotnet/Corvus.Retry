// <copyright file="ConstructionBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of every public constructor. Strategies hold no state for a call, so a caller can create one and share
/// it; a caller that creates a new strategy for each operation pays this cost on every call.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ConstructionBenchmarks
{
    private static readonly InvalidOperationException Failure = new("Transient failure");

    // Shared arguments, so that the benchmarks measure only the construction, not the creation of their arguments.
    private static readonly IRetryStrategy SharedStrategy = new Count(10);
    private static readonly IRetryPolicy SharedPolicy = new AnyExceptionPolicy();
    private static readonly Func<RetryAttempt, TimeSpan?> GetRetryDelay = static attempt => attempt.AttemptNumber < 10 ? TimeSpan.Zero : null;
    private static readonly Func<Exception, bool> IsTransient = static ex => ex is TimeoutException;
    private static readonly Func<TimeoutException, bool> IsTimeoutTransient = static ex => ex.Message.Length > 0;
    private static readonly RetryPipelineOptions PipelineOptions = new() { Strategy = SharedStrategy, AttemptTimeout = TimeSpan.FromSeconds(5) };
    private static readonly RetryPipelineOptions<int> PipelineOptionsOfT = new() { Strategy = SharedStrategy, ShouldRetryResult = static r => r < 0 };
    private static readonly CircuitBreakerOptions BreakerOptions = new() { MinimumThroughput = 10, FailureRatio = 0.5 };
    private static readonly RetryPipelineOptions<int> PipelineOptionsWithFallback = PipelineOptionsOfT with
    {
        Fallback = new FallbackOptions<int> { Action = static _ => new ValueTask<int>(0) },
    };

    private static readonly RetryPipelineOptions<int> PipelineOptionsWithHedging = new()
    {
        Hedging = new HedgingOptions { MaxHedgedAttempts = 2 },
        HedgingActionGenerator = static _ => null,
    };

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Count_Default() => new Count();

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Count_MaxTries() => new Count(10);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy DoNotRetry() => new DoNotRetry();

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Linear() => new Linear(TimeSpan.FromSeconds(1), 10);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Incremental_Default() => new Incremental();

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Incremental_MaxTries_Delays() => new Incremental(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Backoff_Default() => new Backoff();

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Backoff_MaxTries_Delta() => new Backoff(10, TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Linear_MaxDelay_Jitter() => new Linear(TimeSpan.FromSeconds(1), 10, TimeSpan.FromSeconds(5), useJitter: true);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Incremental_MaxDelay_Jitter() => new Incremental(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), useJitter: true);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Exponential() => new Exponential(10, TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy Exponential_MaxDelay_Jitter() => new Exponential(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), useJitter: true);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy RetryStrategies_Constant() => RetryStrategies.Constant(9, TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy RetryStrategies_Linear() => RetryStrategies.Linear(9, TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy RetryStrategies_Exponential() => RetryStrategies.Exponential(9, TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy RetryStrategies_FromDelegate() => RetryStrategies.FromDelegate(GetRetryDelay);

    [Benchmark]
    [BenchmarkCategory("Strategies")]
    public IRetryStrategy RetryStrategies_RetryAfter() => RetryStrategies.RetryAfter(SharedStrategy);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy AnyExceptionPolicy() => new AnyExceptionPolicy();

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy DoNotRetryPolicy() => new DoNotRetryPolicy();

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy AggregatePolicy_WithTwoPolicies() =>
        new AggregatePolicy { Policies = { new AnyExceptionPolicy(), new AnyExceptionPolicy() } };

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_FromPredicate() => RetryPolicies.FromPredicate(IsTransient);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_Handle() => RetryPolicies.Handle<TimeoutException>();

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_Handle_WithPredicate() => RetryPolicies.Handle(IsTimeoutTransient);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_Handle_Or() => RetryPolicies.Handle<TimeoutException>().Or<InvalidOperationException>();

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_HandleInner() => RetryPolicies.HandleInner<TimeoutException>();

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_Not() => RetryPolicies.Not(SharedPolicy);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_Any_Three() => RetryPolicies.Any(SharedPolicy, SharedPolicy, SharedPolicy);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy RetryPolicies_All_Three() => RetryPolicies.All(SharedPolicy, SharedPolicy, SharedPolicy);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy Extension_Or() => SharedPolicy.Or(SharedPolicy);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy Extension_And() => SharedPolicy.And(SharedPolicy);

    [Benchmark]
    [BenchmarkCategory("Policies")]
    public IRetryPolicy Extension_Not() => SharedPolicy.Not();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Other")]
    public RetryEventArgs RetryEventArgs() => new(Failure, null, TimeSpan.FromSeconds(1), 1);

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryTimeoutException RetryTimeoutException() => new(TimeSpan.FromSeconds(1));

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryTaskFactory RetryTaskFactory() => new();

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryTaskFactory<int> RetryTaskFactoryOfT() => new();

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryPipeline RetryPipeline() => new(PipelineOptions);

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryPipeline<int> RetryPipelineOfT() => new(PipelineOptionsOfT);

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryPipeline<int> RetryPipelineOfT_WithFallback() => new(PipelineOptionsWithFallback);

    [Benchmark]
    [BenchmarkCategory("Other")]
    public RetryPipeline<int> RetryPipelineOfT_WithHedging() => new(PipelineOptionsWithHedging);

    [Benchmark]
    [BenchmarkCategory("Other")]
    public CircuitBreaker CircuitBreaker() => new(BreakerOptions);
}
