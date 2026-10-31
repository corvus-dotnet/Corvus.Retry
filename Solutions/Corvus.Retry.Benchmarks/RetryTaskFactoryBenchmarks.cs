// <copyright file="RetryTaskFactoryBenchmarks.cs" company="Endjin Limited">
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
/// The cost of running an operation through <see cref="RetryTaskFactory.Run(Func{CancellationToken, Task}, CancellationToken)"/>,
/// <see cref="RetryTaskFactory.RunValueTask{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/> and the
/// methods of <see cref="RetryTaskFactory{T}"/>, compared with a hand-written retry loop that does the same work.
/// </summary>
/// <remarks>
/// <para>
/// Every operation completes synchronously, and the strategies have no delay, so the benchmarks measure the retry
/// machinery, the timeouts and the exceptions, never a delay. Each timeout is long enough that it never expires.
/// </para>
/// <para>
/// The "Success" category has no failures. <c>RunValueTask</c> on a factory without timeouts must allocate nothing
/// there. The <c>Run</c> benchmarks give the same operations as a <see cref="Task"/>; their operations return a task
/// that is created once, so the benchmarks measure only the library. The "Failures" category has two failures before
/// the success: two exceptions, or one rejected result.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RetryTaskFactoryBenchmarks
{
    private const int MaxTries = 5;
    private const int Result = 42;
    private const int RejectedResult = -1;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(1);
    private static readonly Task<int> CompletedResult = Task.FromResult(Result);
    private static readonly Task<int> CompletedRejectedResult = Task.FromResult(RejectedResult);

    private readonly RetryTaskFactory defaultFactory = new();
    private readonly RetryTaskFactory countFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default);
    private readonly RetryTaskFactory attemptTimeoutFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { AttemptTimeout = LongTimeout };
    private readonly RetryTaskFactory totalTimeoutFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { TotalTimeout = LongTimeout };
    private readonly RetryTaskFactory bothTimeoutsFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { AttemptTimeout = LongTimeout, TotalTimeout = LongTimeout };
    private readonly RetryTaskFactory onRetryFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { OnRetry = _ => ValueTask.CompletedTask };
    private readonly RetryTaskFactory capturedContextFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { ContinueOnCapturedContext = true };
    private readonly RetryTaskFactory<int> resultFactory = new(new Count(MaxTries), AnyExceptionPolicy.Default) { CanRetryResult = static r => r == RejectedResult };

    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask> unitOperation;
    private readonly Func<CancellationToken, ValueTask<int>> resultOperation;
    private readonly Func<CancellationToken, Task<int>> taskOperation;
    private readonly Func<CancellationToken, Task> taskUnitOperation;
    private readonly Func<CancellationToken, Task<int>> taskResultOperation;
    private int remainingFailures;

    public RetryTaskFactoryBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = this.Operation;
        this.unitOperation = this.UnitOperation;
        this.resultOperation = this.ResultOperation;
        this.taskOperation = this.TaskOperation;
        this.taskUnitOperation = this.TaskUnitOperation;
        this.taskResultOperation = this.TaskResultOperation;
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
    public ValueTask<int> RunValueTask_NoOptions()
    {
        this.remainingFailures = 0;
        return this.defaultFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public Task<int> Run_NoOptions()
    {
        this.remainingFailures = 0;
        return this.defaultFactory.Run(this.taskOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask RunValueTask_Unit()
    {
        this.remainingFailures = 0;
        return this.defaultFactory.RunValueTask(this.unitOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public Task Run_Unit()
    {
        this.remainingFailures = 0;
        return this.defaultFactory.Run(this.taskUnitOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> RunValueTask_AttemptTimeout()
    {
        this.remainingFailures = 0;
        return this.attemptTimeoutFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> RunValueTask_TotalTimeout()
    {
        this.remainingFailures = 0;
        return this.totalTimeoutFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> RunValueTask_BothTimeouts()
    {
        this.remainingFailures = 0;
        return this.bothTimeoutsFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> RunValueTask_ContinueOnCapturedContext()
    {
        this.remainingFailures = 0;
        return this.capturedContextFactory.RunValueTask(this.operation);
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
    public ValueTask<int> RunValueTask_WithFailures()
    {
        this.remainingFailures = 2;
        return this.countFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public Task<int> Run_WithFailures()
    {
        this.remainingFailures = 2;
        return this.countFactory.Run(this.taskOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> RunValueTask_OnRetry()
    {
        this.remainingFailures = 2;
        return this.onRetryFactory.RunValueTask(this.operation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public ValueTask<int> RunValueTaskOfT_ResultRetry()
    {
        this.remainingFailures = 1;
        return this.resultFactory.RunValueTask(this.resultOperation);
    }

    [Benchmark]
    [BenchmarkCategory("Failures")]
    public Task<int> RunOfT_ResultRetry()
    {
        this.remainingFailures = 1;
        return this.resultFactory.Run(this.taskResultOperation);
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

    private Task<int> TaskOperation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures-- > 0)
        {
            throw NewFailure();
        }

        return CompletedResult;
    }

    private Task TaskUnitOperation(CancellationToken cancellationToken)
    {
        if (this.remainingFailures-- > 0)
        {
            throw NewFailure();
        }

        return Task.CompletedTask;
    }

    private Task<int> TaskResultOperation(CancellationToken cancellationToken)
    {
        return this.remainingFailures-- > 0 ? CompletedRejectedResult : CompletedResult;
    }
}
