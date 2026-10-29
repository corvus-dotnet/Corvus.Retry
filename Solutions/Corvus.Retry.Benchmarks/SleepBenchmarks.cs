// <copyright file="SleepBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Async;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of the delay path: a retry whose strategy asks for a delay, which the library waits for using its
/// default <see cref="ISleepService"/>.
/// </summary>
/// <remarks>
/// <see cref="ISleepService"/> has no public implementation or injection point, so it is measured through the
/// retry methods that use it. The delay is one tick, which the default service rounds down to no wait, so the
/// benchmarks measure the sleep machinery rather than time passing. The baselines retry with no delay, which skips
/// the sleep service.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkDotNet.Attributes.CategoriesColumn]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class SleepBenchmarks
{
    private const int Result = 42;

    private static readonly Task<int> CompletedResult = Task.FromResult(Result);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private readonly AnyExceptionPolicy policy = new();
    private bool failNext;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Sync")]
    public int Retry_WithoutDelay()
    {
        this.failNext = true;
        return Retriable.Retry(this.Operation, CancellationToken.None, new Linear(TimeSpan.Zero, 2), this.policy);
    }

    [Benchmark]
    [BenchmarkCategory("Sync")]
    public int Retry_WithDelay_Sleep()
    {
        this.failNext = true;
        return Retriable.Retry(this.Operation, CancellationToken.None, new Linear(OneTick, 2), this.policy);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Async")]
    public Task<int> RetryAsync_WithoutDelay()
    {
        this.failNext = true;
        return Retriable.RetryAsync(this.OperationAsync, CancellationToken.None, new Linear(TimeSpan.Zero, 2), this.policy);
    }

    [Benchmark]
    [BenchmarkCategory("Async")]
    public Task<int> RetryAsync_WithDelay_SleepAsync()
    {
        this.failNext = true;
        return Retriable.RetryAsync(this.OperationAsync, CancellationToken.None, new Linear(OneTick, 2), this.policy);
    }

    private int Operation()
    {
        if (this.failNext)
        {
            this.failNext = false;
            throw NewFailure();
        }

        return Result;
    }

    private Task<int> OperationAsync()
    {
        if (this.failNext)
        {
            this.failNext = false;
            return Task.FromException<int>(NewFailure());
        }

        return CompletedResult;
    }

    // Each failure is a new exception: rethrowing one instance from a faulted task appends to its stack trace on
    // every await, so a shared instance grows without bound and distorts time and allocation.
    private static InvalidOperationException NewFailure() => new("Transient failure");
}
