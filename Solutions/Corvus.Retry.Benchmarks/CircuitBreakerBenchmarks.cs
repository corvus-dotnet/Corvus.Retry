// <copyright file="CircuitBreakerBenchmarks.cs" company="Endjin Limited">
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
/// The cost of a <see cref="CircuitBreaker"/> in a <see cref="RetryTaskFactory"/> that makes one attempt, compared with the
/// same factory without a breaker.
/// </summary>
/// <remarks>
/// <para>
/// Every operation completes synchronously. The "Closed" category has a closed circuit and an operation that succeeds;
/// the breaker must allocate nothing there. The "Failure" category has a closed circuit that never opens and an
/// operation that fails. The "Rejected" category has a circuit that rejects the call, so the operation does not run.
/// </para>
/// <para>
/// <c>HalfOpen_Probe</c> moves the clock past the open duration before each call, so that the call is the test call of
/// a half-open circuit. The test call fails, and the circuit opens again.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CircuitBreakerBenchmarks
{
    private const int Result = 42;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(1);

    private readonly ManualTimeProvider clock = new();
    private readonly RetryTaskFactory noBreakerFactory;
    private readonly RetryTaskFactory closedFactory;
    private readonly RetryTaskFactory openFactory;
    private readonly RetryTaskFactory isolatedFactory;
    private readonly RetryTaskFactory probeFactory;
    private readonly Func<CancellationToken, ValueTask<int>> operation;
    private readonly Func<CancellationToken, ValueTask<int>> failingOperation;

    public CircuitBreakerBenchmarks()
    {
        // The delegates are created once, so that the benchmarks do not measure their allocation.
        this.operation = static _ => new ValueTask<int>(Result);
        this.failingOperation = static _ => throw new InvalidOperationException("Failure");

        // A minimum number of calls that is never reached keeps the circuit closed.
        var closedBreaker = new CircuitBreaker(minimumCalls: int.MaxValue);
        var openBreaker = new CircuitBreaker(minimumCalls: 2, openDuration: OpenDuration, timeProvider: this.clock);
        var isolatedBreaker = new CircuitBreaker();
        isolatedBreaker.Isolate();

        this.noBreakerFactory = NewFactory(null);
        this.closedFactory = NewFactory(closedBreaker);
        this.openFactory = NewFactory(openBreaker);
        this.isolatedFactory = NewFactory(isolatedBreaker);
        this.probeFactory = NewFactory(new CircuitBreaker(minimumCalls: 2, openDuration: OpenDuration, timeProvider: this.clock));

        // Two failures open each circuit. Only HalfOpen_Probe moves the clock.
        for (int i = 0; i < 2; i++)
        {
            _ = Observe(this.openFactory.RunValueTask(this.failingOperation));
            _ = Observe(this.probeFactory.RunValueTask(this.failingOperation));
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Closed")]
    public ValueTask<int> NoBreaker_Success() => this.noBreakerFactory.RunValueTask(this.operation);

    [Benchmark]
    [BenchmarkCategory("Closed")]
    public ValueTask<int> Closed_Success() => this.closedFactory.RunValueTask(this.operation);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Failure")]
    public int NoBreaker_Failure() => Observe(this.noBreakerFactory.RunValueTask(this.failingOperation));

    [Benchmark]
    [BenchmarkCategory("Failure")]
    public int Closed_Failure() => Observe(this.closedFactory.RunValueTask(this.failingOperation));

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Rejected")]
    public int Open_Rejected() => Observe(this.openFactory.RunValueTask(this.operation));

    [Benchmark]
    [BenchmarkCategory("Rejected")]
    public int Isolated_Rejected() => Observe(this.isolatedFactory.RunValueTask(this.operation));

    [Benchmark]
    [BenchmarkCategory("Rejected")]
    public int HalfOpen_Probe()
    {
        this.clock.Advance(OpenDuration);
        return Observe(this.probeFactory.RunValueTask(this.failingOperation));
    }

    private static RetryTaskFactory NewFactory(CircuitBreaker? breaker)
    {
        return new RetryTaskFactory(new DoNotRetry(), AnyExceptionPolicy.Default) { CircuitBreaker = breaker };
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
        catch (CircuitRejectedException)
        {
            return -2;
        }
    }

    /// <summary>
    /// A clock that moves only when the benchmark moves it.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        // The breaker reads the monotonic timestamp, so this clock gives a timestamp in ticks.
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => this.timestamp;

        public void Advance(TimeSpan time) => this.timestamp += time.Ticks;
    }
}
