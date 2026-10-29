// <copyright file="RetryPipelineBenchmarks.cs" company="Endjin Limited">
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
/// The cost of running an operation through a <see cref="RetryPipeline"/> or a <see cref="RetryPipeline{T}"/>,
/// compared with a hand-written retry loop that does the same work.
/// </summary>
/// <remarks>
/// <para>
/// Every operation completes synchronously, and the strategies have no delay, so the benchmarks measure the retry
/// machinery, the timeouts and the exceptions, never a delay. Each timeout is long enough that it never expires.
/// </para>
/// <para>
/// The "Success" category has no failures. A pipeline without timeouts must allocate nothing there. The "Failures"
/// category has two failures before the success: two exceptions, or one rejected result.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RetryPipelineBenchmarks
{
    private const int MaxTries = 5;
    private const int Result = 42;
    private const int RejectedResult = -1;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(1);

    private readonly RetryPipeline defaultPipeline = new(new RetryPipelineOptions());
    private readonly RetryPipeline countPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries) });
    private readonly RetryPipeline attemptTimeoutPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries), AttemptTimeout = LongTimeout });
    private readonly RetryPipeline totalTimeoutPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries), TotalTimeout = LongTimeout });
    private readonly RetryPipeline bothTimeoutsPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries), AttemptTimeout = LongTimeout, TotalTimeout = LongTimeout });
    private readonly RetryPipeline onRetryPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries), OnRetry = _ => ValueTask.CompletedTask });
    private readonly RetryPipeline capturedContextPipeline = new(new RetryPipelineOptions { Strategy = new Count(MaxTries), ContinueOnCapturedContext = true });
    private readonly RetryPipeline<int> resultPipeline = new(new RetryPipelineOptions<int> { Strategy = new Count(MaxTries), ShouldRetryResult = static r => r == RejectedResult });

    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask> unitOperation;
    private readonly Func<CancellationToken, ValueTask<int>> resultOperation;
    private int remainingFailures;

    public RetryPipelineBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = this.Operation;
        this.unitOperation = this.UnitOperation;
        this.resultOperation = this.ResultOperation;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Success")]
    public ValueTask<int> HandWrittenLoop()
    {
        this.remainingFailures = 0;
        return this.HandWrittenLoopAsync();
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Execute_NoOptions()
    {
        this.remainingFailures = 0;
        return this.defaultPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask Execute_Unit()
    {
        this.remainingFailures = 0;
        return this.defaultPipeline.ExecuteAsync(this.unitOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Execute_AttemptTimeout()
    {
        this.remainingFailures = 0;
        return this.attemptTimeoutPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Execute_TotalTimeout()
    {
        this.remainingFailures = 0;
        return this.totalTimeoutPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Execute_BothTimeouts()
    {
        this.remainingFailures = 0;
        return this.bothTimeoutsPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Execute_ContinueOnCapturedContext()
    {
        this.remainingFailures = 0;
        return this.capturedContextPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> HandWrittenLoop_WithFailures()
    {
        this.remainingFailures = 2;
        return this.HandWrittenLoopAsync();
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> Execute_WithFailures()
    {
        this.remainingFailures = 2;
        return this.countPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> Execute_OnRetry()
    {
        this.remainingFailures = 2;
        return this.onRetryPipeline.ExecuteAsync(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> ExecuteOfT_ResultRetry()
    {
        this.remainingFailures = 1;
        return this.resultPipeline.ExecuteAsync(this.resultOperation);
    }

    // Each failure is a new exception: rethrowing one instance appends to its stack trace on every throw, so a shared
    // instance grows without bound and distorts time and allocation.
    private static InvalidOperationException NewFailure() => new("Transient failure");

    private async ValueTask<int> HandWrittenLoopAsync()
    {
        for (int tries = 1; ; tries++)
        {
            CancellationToken.None.ThrowIfCancellationRequested();
            try
            {
                return await this.Operation(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (tries < MaxTries)
            {
            }
        }
    }

    private ValueTask<int> Operation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures-- > 0)
        {
            throw NewFailure();
        }

        return new ValueTask<int>(Result);
    }

    private ValueTask UnitOperation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures-- > 0)
        {
            throw NewFailure();
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask<int> ResultOperation(CancellationToken cancellationToken)
    {
        return new ValueTask<int>(this.remainingFailures-- > 0 ? RejectedResult : Result);
    }
}
