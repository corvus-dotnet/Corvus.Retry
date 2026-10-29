// <copyright file="HedgingBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of a hedged call through a <see cref="ReliabilityPipeline{T}"/>, compared with the same operation through a
/// pipeline that retries.
/// </summary>
/// <remarks>
/// <para>
/// The "Success" category has an operation that succeeds at once, so no hedged attempt starts. The "Failure" category
/// has an operation that fails once and then succeeds: the retry pipeline retries with no delay, and the hedging
/// pipeline starts the second attempt immediately. In these categories every operation completes synchronously, and
/// the hedging delay is long enough that it never expires, so the benchmarks measure the hedging machinery, never a
/// delay.
/// </para>
/// <para>
/// The "Parallel" category has a zero delay, and a first attempt that is not complete when the pipeline reads it, so
/// the pipeline starts the second attempt at once, on the thread pool. A first attempt that completes synchronously
/// would win before the second attempt starts, so each of these benchmarks allocates the task that keeps its first
/// attempt pending. In <see cref="Hedged_DelayZero_BothComplete"/> the second attempt completes the first and then
/// succeeds at once: both attempts complete, and the pipeline returns the first outcome that it reads and discards the
/// other. In <see cref="Hedged_DelayZero_LoserCancelled"/> the second attempt wins at once, and the first waits until
/// the pipeline cancels it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class HedgingBenchmarks
{
    private const int Result = 42;

    private readonly ReliabilityPipeline<int> retryPipeline = new(new ReliabilityPipelineOptions<int> { Strategy = new Count(2) });
    private readonly ReliabilityPipeline<int> hedgingPipeline = new(new ReliabilityPipelineOptions<int>
    {
        Hedging = new HedgingOptions { Delay = TimeSpan.FromMinutes(1) },
    });

    private readonly ReliabilityPipeline<int> parallelPipeline;
    private readonly ReliabilityPipeline<int> parallelLoserPipeline;

    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask<int>> flakyOperation;
    private readonly Func<CancellationToken, ValueTask<int>> completePendingAttemptThenSucceed;
    private int remainingFailures;

    // The task of the first attempt of the current call of Hedged_DelayZero_BothComplete.
    private TaskCompletionSource<int>? pendingFirstAttempt;

    public HedgingBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => new ValueTask<int>(Result);
        this.flakyOperation = this.FlakyOperation;
        this.completePendingAttemptThenSucceed = this.CompletePendingAttemptThenSucceed;

        // The first attempt is pending until the second attempt starts. The second attempt completes the first, and then
        // succeeds at once.
        Func<CancellationToken, ValueTask<int>> startPendingAttempt = this.StartPendingAttempt;
        this.parallelPipeline = new(new ReliabilityPipelineOptions<int>
        {
            Hedging = new HedgingOptions { Delay = TimeSpan.Zero },
            HedgingActionGenerator = attempt => attempt == 1 ? startPendingAttempt : null,
        });

        // The first attempt waits until its token is cancelled. The second attempt wins at once.
        this.parallelLoserPipeline = new(new ReliabilityPipelineOptions<int>
        {
            Hedging = new HedgingOptions { Delay = TimeSpan.Zero },
            HedgingActionGenerator = static attempt => attempt == 1 ? WaitUntilCancelledAsync : null,
        });
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Retry_Success() => this.retryPipeline.ExecuteAsync(this.operation);

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Hedged_FirstAttemptSucceeds() => this.hedgingPipeline.ExecuteAsync(this.operation);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Failure")]
    public ValueTask<int> Retry_FirstFails_SecondSucceeds()
    {
        this.remainingFailures = 1;
        return this.retryPipeline.ExecuteAsync(this.flakyOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Failure")]
    public ValueTask<int> Hedged_FirstFails_SecondSucceeds()
    {
        this.remainingFailures = 1;
        return this.hedgingPipeline.ExecuteAsync(this.flakyOperation);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Parallel")]
    public ValueTask<int> Hedged_DelayZero_BothComplete() => this.parallelPipeline.ExecuteAsync(this.completePendingAttemptThenSucceed);

    [Benchmark]
    [BenchmarkCategory("Parallel")]
    public ValueTask<int> Hedged_DelayZero_LoserCancelled() => this.parallelLoserPipeline.ExecuteAsync(this.operation);

    private static async ValueTask<int> WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return Result;
    }

    private ValueTask<int> StartPendingAttempt(CancellationToken cancellationToken)
    {
        // The continuation of the first attempt runs on the thread pool, not inside the operation of the second attempt.
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingFirstAttempt = pending;
        return new ValueTask<int>(pending.Task);
    }

    private ValueTask<int> CompletePendingAttemptThenSucceed(CancellationToken cancellationToken)
    {
        this.pendingFirstAttempt!.SetResult(Result);
        return new ValueTask<int>(Result);
    }

    private ValueTask<int> FlakyOperation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures > 0)
        {
            this.remainingFailures--;
            throw new InvalidOperationException("Failure");
        }

        return new ValueTask<int>(Result);
    }
}
