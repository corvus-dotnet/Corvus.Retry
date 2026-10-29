// <copyright file="RetriableBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of wrapping an operation in <see cref="Retriable"/>, compared with a hand-written retry loop that does
/// the same work.
/// </summary>
/// <remarks>
/// The operation fails <see cref="Failures"/> times before it succeeds. The <see cref="Count"/> strategy has no
/// delay, so the benchmarks measure the retry machinery and the exceptions, never a sleep.
/// The "Token" category measures the overload whose operation takes a cancellation token, with and without a
/// retry callback.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RetriableBenchmarks
{
    private const int MaxTries = 5;
    private const int Result = 42;

    private static readonly Task<int> CompletedResult = Task.FromResult(Result);
    private static readonly Func<RetryEventArgs, ValueTask> NoOpOnRetry = _ => ValueTask.CompletedTask;

    private readonly AnyExceptionPolicy policy = new();
    private int remainingFailures;

    /// <summary>
    /// Gets or sets the number of times the operation fails before it succeeds.
    /// </summary>
    [Params(0, 2)]
    public int Failures { get; set; }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Sync")]
    public int HandWrittenLoop()
    {
        this.remainingFailures = this.Failures;
        for (int tries = 1; ; tries++)
        {
            try
            {
                return this.Operation();
            }
            catch (Exception) when (tries < MaxTries)
            {
            }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Sync")]
    public int Retry()
    {
        this.remainingFailures = this.Failures;
        return Retriable.Retry(this.Operation, CancellationToken.None, new Count(MaxTries), this.policy);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Async")]
    public async Task<int> HandWrittenLoopAsync()
    {
        this.remainingFailures = this.Failures;
        for (int tries = 1; ; tries++)
        {
            try
            {
                return await this.OperationAsync().ConfigureAwait(false);
            }
            catch (Exception) when (tries < MaxTries)
            {
            }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Async")]
    public Task<int> RetryAsync()
    {
        this.remainingFailures = this.Failures;
        return Retriable.RetryAsync(this.OperationAsync, CancellationToken.None, new Count(MaxTries), this.policy);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Token")]
    public async Task<int> HandWrittenLoopWithTokenAsync()
    {
        this.remainingFailures = this.Failures;
        for (int tries = 1; ; tries++)
        {
            CancellationToken.None.ThrowIfCancellationRequested();
            try
            {
                return await this.OperationWithTokenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (tries < MaxTries)
            {
            }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Token")]
    public Task<int> RetryAsync_Token()
    {
        this.remainingFailures = this.Failures;
        return Retriable.RetryAsync(this.OperationWithTokenAsync, CancellationToken.None, new Count(MaxTries), this.policy);
    }

    [Benchmark]
    [BenchmarkCategory("Token")]
    public Task<int> RetryAsync_Token_OnRetry()
    {
        this.remainingFailures = this.Failures;
        return Retriable.RetryAsync(this.OperationWithTokenAsync, CancellationToken.None, new Count(MaxTries), this.policy, false, NoOpOnRetry);
    }

    private int Operation()
    {
        if (this.remainingFailures-- > 0)
        {
            throw NewFailure();
        }

        return Result;
    }

    private Task<int> OperationAsync()
    {
        return this.remainingFailures-- > 0 ? Task.FromException<int>(NewFailure()) : CompletedResult;
    }

    private Task<int> OperationWithTokenAsync(CancellationToken cancellationToken)
    {
        return this.OperationAsync();
    }

    // Each failure is a new exception: rethrowing one instance from a faulted task appends to its stack trace on
    // every await, so a shared instance grows without bound and distorts time and allocation.
    private static InvalidOperationException NewFailure() => new("Transient failure");
}
