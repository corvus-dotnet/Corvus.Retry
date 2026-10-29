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
/// The cost of a hedged call through a <see cref="RetryPipeline{T}"/>, compared with the same operation through a
/// pipeline that retries.
/// </summary>
/// <remarks>
/// <para>
/// The "Success" category has an operation that succeeds at once, so no hedged attempt starts. The "Failure" category
/// has an operation that fails once and then succeeds: the retry pipeline retries with no delay, and the hedging
/// pipeline starts the second attempt immediately. The "Parallel" category starts two attempts at once: in
/// <see cref="Hedged_DelayZero_TwoAttempts"/> both complete and the first wins, and in
/// <see cref="Hedged_DelayZero_LoserCancelled"/> the first attempt waits until the pipeline cancels it.
/// </para>
/// <para>
/// Every operation that is not cancelled completes synchronously, and the hedging delay is long enough that it never
/// expires, so the benchmarks measure the hedging machinery, never a delay.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class HedgingBenchmarks
{
    private const int Result = 42;

    private readonly RetryPipeline<int> retryPipeline = new(new RetryPipelineOptions<int> { Strategy = new Count(2) });
    private readonly RetryPipeline<int> hedgingPipeline = new(new RetryPipelineOptions<int>
    {
        Hedging = new HedgingOptions { Delay = TimeSpan.FromMinutes(1) },
    });

    private readonly RetryPipeline<int> parallelPipeline = new(new RetryPipelineOptions<int>
    {
        Hedging = new HedgingOptions { Delay = TimeSpan.Zero },
    });

    private readonly RetryPipeline<int> parallelLoserPipeline;

    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask<int>> flakyOperation;
    private int remainingFailures;

    public HedgingBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => new ValueTask<int>(Result);
        this.flakyOperation = this.FlakyOperation;

        // The first attempt waits until its token is cancelled. The second attempt wins at once.
        this.parallelLoserPipeline = new(new RetryPipelineOptions<int>
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
    public ValueTask<int> Hedged_DelayZero_TwoAttempts() => this.parallelPipeline.ExecuteAsync(this.operation);

    [Benchmark]
    [BenchmarkCategory("Parallel")]
    public ValueTask<int> Hedged_DelayZero_LoserCancelled() => this.parallelLoserPipeline.ExecuteAsync(this.operation);

    private static async ValueTask<int> WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return Result;
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
