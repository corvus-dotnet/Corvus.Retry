// <copyright file="ConstructionBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
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
}
