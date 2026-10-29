// <copyright file="RetryTaskBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of starting an operation with <see cref="RetryTask{T}"/>, compared with starting the same hand-written
/// retry loop with <see cref="Task.Factory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both run on the thread pool, so the comparison includes the extra continuation and completion source that
/// <see cref="RetryTask{T}"/> uses to report the outcome of the operation as a whole.
/// </para>
/// <para>
/// Most of the gap on the failure path is the runtime's, not the library's. Each try runs as its own task, and
/// when an execution context flows, the runtime catches a failed try's exception, captures it and throws it again,
/// so every failure costs two throws rather than the loop's one. The thread pool hops also make these timings
/// bimodal (they depend on whether a worker thread must be woken), so compare medians and expect a wide spread.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RetryTaskBenchmarks
{
    private const int MaxTries = 5;
    private const int Result = 42;

    private readonly AnyExceptionPolicy policy = new();
    private int remainingFailures;

    /// <summary>
    /// Gets or sets the number of times the operation fails before it succeeds.
    /// </summary>
    [Params(0, 2)]
    public int Failures { get; set; }

    [Benchmark(Baseline = true)]
    public Task<int> TaskFactoryWithHandWrittenLoop()
    {
        this.remainingFailures = this.Failures;
        return Task<int>.Factory.StartNew(this.HandWrittenLoop);
    }

    [Benchmark]
    public Task<int> RetryTaskFactory()
    {
        this.remainingFailures = this.Failures;
        return RetryTask<int>.Factory.StartNew(this.Operation, new Count(MaxTries), this.policy);
    }

    /// <summary>
    /// Retries through the delay continuation. A one-tick delay rounds down to no wait, so this measures the
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, System.Threading.CancellationToken)"/> machinery, not time.
    /// </summary>
    /// <returns>A task which completes when the operation is complete.</returns>
    [Benchmark]
    public Task<int> RetryTaskFactory_WithOneTickDelay()
    {
        this.remainingFailures = this.Failures;
        return RetryTask<int>.Factory.StartNew(this.Operation, new Linear(TimeSpan.FromTicks(1), MaxTries), this.policy);
    }

    private int HandWrittenLoop()
    {
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

    private int Operation()
    {
        if (this.remainingFailures-- > 0)
        {
            // A new exception each time: a task stores the exception it fails with, and rethrowing a shared
            // instance from a faulted task appends to its stack trace on every wait.
            throw new InvalidOperationException("Transient failure");
        }

        return Result;
    }
}
