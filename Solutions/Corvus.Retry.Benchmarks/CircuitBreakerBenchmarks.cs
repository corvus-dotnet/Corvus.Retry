// <copyright file="CircuitBreakerBenchmarks.cs" company="Endjin Limited">
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
/// The cost of a <see cref="CircuitBreaker"/> in a <see cref="RetryPipeline"/> that makes one attempt, compared with the
/// same pipeline without a breaker.
/// </summary>
/// <remarks>
/// <para>
/// Every operation completes synchronously. The "Closed" category has a closed circuit and an operation that succeeds;
/// the breaker must allocate nothing there. The "Failure" category has a closed circuit that never opens and an
/// operation that fails. The "Rejected" category has a circuit that rejects the call, so the operation does not run.
/// </para>
/// <para>
/// <c>HalfOpen_Probe</c> moves the clock past the break before each call, so that the call is the test call of a
/// half-open circuit. The test call fails, and the circuit opens again.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CircuitBreakerBenchmarks
{
    private const int Result = 42;

    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(1);

    private readonly ManualTimeProvider clock = new();
    private readonly RetryPipeline noBreakerPipeline;
    private readonly RetryPipeline closedPipeline;
    private readonly RetryPipeline openPipeline;
    private readonly RetryPipeline isolatedPipeline;
    private readonly RetryPipeline probePipeline;
    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask<int>> failingOperation;

    public CircuitBreakerBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => new ValueTask<int>(Result);
        this.failingOperation = static _ => throw new InvalidOperationException("Failure");

        // A minimum throughput that is never reached keeps the circuit closed.
        var closedBreaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = int.MaxValue });
        var openBreaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, BreakDuration = BreakDuration, TimeProvider = this.clock });
        var isolatedBreaker = new CircuitBreaker(new CircuitBreakerOptions());
        isolatedBreaker.Isolate();

        this.noBreakerPipeline = NewPipeline(null);
        this.closedPipeline = NewPipeline(closedBreaker);
        this.openPipeline = NewPipeline(openBreaker);
        this.isolatedPipeline = NewPipeline(isolatedBreaker);
        this.probePipeline = NewPipeline(new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, BreakDuration = BreakDuration, TimeProvider = this.clock }));

        // Two failures open each circuit. Only HalfOpen_Probe moves the clock.
        for (int i = 0; i < 2; i++)
        {
            _ = Observe(this.openPipeline.ExecuteAsync(this.failingOperation));
            _ = Observe(this.probePipeline.ExecuteAsync(this.failingOperation));
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Closed")]
    public ValueTask<int> NoBreaker_Success() => this.noBreakerPipeline.ExecuteAsync(this.operation);

    [Benchmark]
    [BenchmarkCategory("Closed")]
    public ValueTask<int> Closed_Success() => this.closedPipeline.ExecuteAsync(this.operation);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Failure")]
    public int NoBreaker_Failure() => Observe(this.noBreakerPipeline.ExecuteAsync(this.failingOperation));

    [Benchmark]
    [BenchmarkCategory("Failure")]
    public int Closed_Failure() => Observe(this.closedPipeline.ExecuteAsync(this.failingOperation));

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Rejected")]
    public int Open_Rejected() => Observe(this.openPipeline.ExecuteAsync(this.operation));

    [Benchmark]
    [BenchmarkCategory("Rejected")]
    public int Isolated_Rejected() => Observe(this.isolatedPipeline.ExecuteAsync(this.operation));

    [Benchmark]
    [BenchmarkCategory("Rejected")]
    public int HalfOpen_Probe()
    {
        this.clock.Advance(BreakDuration);
        return Observe(this.probePipeline.ExecuteAsync(this.failingOperation));
    }

    private static RetryPipeline NewPipeline(CircuitBreaker? breaker)
    {
        return new RetryPipeline(new RetryPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker });
    }

    // Every call completes synchronously, so the exception is read without a wait.
    private static int Observe(ValueTask<int> call)
    {
        try
        {
            return call.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
        catch (BrokenCircuitException)
        {
            return -2;
        }
    }

    /// <summary>
    /// A clock that moves only when the benchmark moves it.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan time) => this.now += time;
    }
}
