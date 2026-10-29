// <copyright file="StrategyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of each built-in strategy deciding on its delays, as a retry loop drives it through a run of failures.
/// </summary>
/// <remarks>
/// Strategies hold no state for a call, so each benchmark uses one strategy created once, and asks it for the delay
/// after each of <see cref="Failures"/> failed attempts, as a retry loop does, until it returns <see langword="null"/>.
/// No benchmark waits for the delays it calculates.
/// </remarks>
[MemoryDiagnoser]
public class StrategyBenchmarks
{
    private const int MaxTries = 10;

    private static readonly InvalidOperationException Failure = new("Transient failure");

    private static readonly Count CountStrategy = new(MaxTries);
    private static readonly DoNotRetry DoNotRetryStrategy = new();
    private static readonly Linear LinearStrategy = new(TimeSpan.FromSeconds(1), MaxTries);
    private static readonly Incremental IncrementalStrategy = new(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    private static readonly Backoff BackoffStrategy = new(MaxTries, TimeSpan.FromSeconds(1));

    // Narrow the default bounds, as a caller tuning the strategy would.
    private static readonly Backoff BackoffWithBoundsStrategy = new(MaxTries, TimeSpan.FromMilliseconds(100))
    {
        MinBackoff = TimeSpan.FromMilliseconds(900),
        MaxBackoff = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Gets or sets the number of failed attempts reported to each strategy.
    /// </summary>
    [Params(1, 9)]
    public int Failures { get; set; }

    [Benchmark(Baseline = true)]
    public TimeSpan Count() => this.Drive(CountStrategy);

    [Benchmark]
    public TimeSpan DoNotRetry() => this.Drive(DoNotRetryStrategy);

    [Benchmark]
    public TimeSpan Linear() => this.Drive(LinearStrategy);

    [Benchmark]
    public TimeSpan Incremental() => this.Drive(IncrementalStrategy);

    [Benchmark]
    public TimeSpan Backoff() => this.Drive(BackoffStrategy);

    [Benchmark]
    public TimeSpan Backoff_WithBounds() => this.Drive(BackoffWithBoundsStrategy);

    [Benchmark]
    public TimeSpan RetryStrategies_Default() => this.Drive(RetryStrategies.Default);

    private TimeSpan Drive(IRetryStrategy strategy)
    {
        TimeSpan total = TimeSpan.Zero;
        TimeSpan previousDelay = TimeSpan.Zero;
        for (int attempt = 1; attempt <= this.Failures; attempt++)
        {
            TimeSpan? delay = strategy.GetRetryDelay(new RetryAttempt(attempt, Failure, null, previousDelay));
            if (delay is not TimeSpan next)
            {
                break;
            }

            total += next;
            previousDelay = next;
        }

        return total;
    }
}
