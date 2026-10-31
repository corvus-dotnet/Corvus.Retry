// <copyright file="HedgingBenchmarks.cs" company="Endjin Limited">
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
/// The cost of a hedged call through <see cref="HedgedTaskFactory{T}.Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>,
/// compared with the same operation through <see cref="RetryTaskFactory{T}.Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>
/// on a factory that retries.
/// </summary>
/// <remarks>
/// <para>
/// Both factories take operations that return a <see cref="Task{TResult}"/>, because a hedged factory has no
/// <c>RunValueTask</c>. Each operation that completes synchronously returns a task that is created once.
/// </para>
/// <para>
/// The "Success" category has an operation that succeeds at once, so no hedged attempt starts. The "Failure" category
/// has an operation that fails once and then succeeds: the retry factory retries with no delay, and the hedged factory
/// starts the second attempt immediately. In these categories every operation completes synchronously, and the stagger
/// is long enough that it never expires, so the benchmarks measure the hedging machinery, never a delay.
/// </para>
/// <para>
/// The "Parallel" category has a stagger of zero, and a first attempt that is not complete when the factory reads it,
/// so the factory starts the second attempt at once, on the thread pool. A first attempt that completes synchronously
/// would win before the second attempt starts, so each of these benchmarks allocates the task that keeps its first
/// attempt pending. In <see cref="Hedged_DelayZero_BothComplete"/> the second attempt completes the first and then
/// succeeds at once: both attempts complete, and the factory returns the first outcome that it reads and discards the
/// other. In <see cref="Hedged_DelayZero_LoserCancelled"/> the second attempt wins at once, and the first waits until
/// the factory cancels it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class HedgingBenchmarks
{
    private const int Result = 42;

    private static readonly Task<int> CompletedResult = Task.FromResult(Result);

    private readonly RetryTaskFactory<int> retryFactory = new(new Count(2), AnyExceptionPolicy.Default);
    private readonly HedgedTaskFactory<int> hedgedFactory = new(extraAttempts: 1, stagger: TimeSpan.FromMinutes(1));

    private readonly HedgedTaskFactory<int> parallelFactory;
    private readonly HedgedTaskFactory<int> parallelLoserFactory;

    private readonly Func<CancellationToken, Task<int>> operation;
    private readonly Func<CancellationToken, Task<int>> flakyOperation;
    private readonly Func<CancellationToken, Task<int>> completePendingAttemptThenSucceed;
    private int remainingFailures;

    // The task of the first attempt of the current call of Hedged_DelayZero_BothComplete.
    private TaskCompletionSource<int>? pendingFirstAttempt;

    public HedgingBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => CompletedResult;
        this.flakyOperation = this.FlakyOperation;
        this.completePendingAttemptThenSucceed = this.CompletePendingAttemptThenSucceed;

        // The first attempt is pending until the second attempt starts. The second attempt completes the first, and then
        // succeeds at once.
        Func<CancellationToken, Task<int>> startPendingAttempt = this.StartPendingAttempt;
        this.parallelFactory = new(extraAttempts: 1, stagger: TimeSpan.Zero)
        {
            OperationForAttempt = attempt => attempt == 1 ? startPendingAttempt : null,
        };

        // The first attempt waits until its token is cancelled. The second attempt wins at once.
        this.parallelLoserFactory = new(extraAttempts: 1, stagger: TimeSpan.Zero)
        {
            OperationForAttempt = static attempt => attempt == 1 ? WaitUntilCancelledAsync : null,
        };
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Success")]
    public Task<int> Retry_Success() => this.retryFactory.Run(this.operation);

    [Benchmark]
    [BenchmarkCategory("Success")]
    public Task<int> Hedged_FirstAttemptSucceeds() => this.hedgedFactory.Run(this.operation);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Failure")]
    public Task<int> Retry_FirstFails_SecondSucceeds()
    {
        this.remainingFailures = 1;
        return this.retryFactory.Run(this.flakyOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Failure")]
    public Task<int> Hedged_FirstFails_SecondSucceeds()
    {
        this.remainingFailures = 1;
        return this.hedgedFactory.Run(this.flakyOperation);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Parallel")]
    public Task<int> Hedged_DelayZero_BothComplete() => this.parallelFactory.Run(this.completePendingAttemptThenSucceed);

    [Benchmark]
    [BenchmarkCategory("Parallel")]
    public Task<int> Hedged_DelayZero_LoserCancelled() => this.parallelLoserFactory.Run(this.operation);

    private static async Task<int> WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return Result;
    }

    private Task<int> StartPendingAttempt(CancellationToken cancellationToken)
    {
        // The continuation of the first attempt runs on the thread pool, not inside the operation of the second attempt.
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingFirstAttempt = pending;
        return pending.Task;
    }

    private Task<int> CompletePendingAttemptThenSucceed(CancellationToken cancellationToken)
    {
        this.pendingFirstAttempt!.SetResult(Result);
        return CompletedResult;
    }

    private Task<int> FlakyOperation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures > 0)
        {
            this.remainingFailures--;
            throw new InvalidOperationException("Failure");
        }

        return CompletedResult;
    }
}
