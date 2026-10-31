// <copyright file="FallbackBenchmarks.cs" company="Endjin Limited">
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
/// The cost of a <see cref="Fallback{T}"/> in a <see cref="RetryTaskFactory{T}"/> that makes one attempt, compared with
/// the same factory without a fallback.
/// </summary>
/// <remarks>
/// <para>
/// Every operation, callback and fallback action completes synchronously. The "Success" category has an operation that
/// succeeds, so the fallback does not apply; a factory without a fallback must allocate nothing there. The "Exception"
/// category has an operation that throws, and the "Result" category has an operation that returns a result that the
/// fallback rejects.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class FallbackBenchmarks
{
    private const int Result = 42;
    private const int RejectedResult = -1;
    private const int FallbackResult = 0;

    private readonly RetryTaskFactory<int> noFallbackFactory;
    private readonly RetryTaskFactory<int> fallbackFactory;
    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask<int>> failingOperation;
    private readonly Func<CancellationToken, ValueTask<int>> rejectedOperation;

    public FallbackBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => new ValueTask<int>(Result);
        this.failingOperation = static _ => throw new InvalidOperationException("Failure");
        this.rejectedOperation = static _ => new ValueTask<int>(RejectedResult);

        this.noFallbackFactory = new RetryTaskFactory<int>(new DoNotRetry(), AnyExceptionPolicy.Default);
        this.fallbackFactory = new RetryTaskFactory<int>(new DoNotRetry(), AnyExceptionPolicy.Default)
        {
            Fallback = new(static _ => new ValueTask<int>(FallbackResult))
            {
                AppliesToResult = static r => r == RejectedResult,
                OnFallback = static _ => ValueTask.CompletedTask,
            },
        };
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Success")]
    public ValueTask<int> NoFallback_Success() => this.noFallbackFactory.RunValueTask(this.operation);

    [Benchmark]
    [BenchmarkCategory("Success")]
    public ValueTask<int> Success_NoFallbackUsed() => this.fallbackFactory.RunValueTask(this.operation);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Exception")]
    public int NoFallback_Exception() => Observe(this.noFallbackFactory.RunValueTask(this.failingOperation));

    [Benchmark]
    [BenchmarkCategory("Exception")]
    public int Exception_FallbackUsed() => Observe(this.fallbackFactory.RunValueTask(this.failingOperation));

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Result")]
    public ValueTask<int> NoFallback_Result() => this.noFallbackFactory.RunValueTask(this.rejectedOperation);

    [Benchmark]
    [BenchmarkCategory("Result")]
    public ValueTask<int> Result_FallbackUsed() => this.fallbackFactory.RunValueTask(this.rejectedOperation);

    // Every call completes synchronously, so the exception is read without a wait.
    private static int Observe(ValueTask<int> call)
    {
        try
        {
            return call.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            return -2;
        }
    }
}
