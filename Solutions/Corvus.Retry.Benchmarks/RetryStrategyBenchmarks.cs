// <copyright file="RetryStrategyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Strategies;

/// <summary>
/// The cost of the <see cref="RetryStrategy"/> members a retry loop uses besides
/// <see cref="IRetryStrategy.PrepareToRetry(Exception)"/>: checking whether it can retry, reading the failures it
/// recorded, and raising <see cref="IRetryStrategy.Retrying"/>.
/// </summary>
/// <remarks>
/// <see cref="RetryStrategy.Exception"/> builds a new <see cref="AggregateException"/> of the recorded failures on
/// every read, so its cost grows with <see cref="RecordedFailures"/>.
/// </remarks>
[MemoryDiagnoser]
public class RetryStrategyBenchmarks
{
    private const int MaxTries = 10;

    private static readonly InvalidOperationException Failure = new("Transient failure");

    private readonly RetryEventArgs eventArgs = new(Failure, TimeSpan.FromSeconds(1));
    private readonly EventHandler<RetryEventArgs> handler = OnRetrying;
    private Count withoutSubscriber = new(MaxTries);
    private Count withSubscriber = new(MaxTries);

    /// <summary>
    /// Gets or sets the number of failures the strategy has recorded.
    /// </summary>
    [Params(1, 9)]
    public int RecordedFailures { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        this.withoutSubscriber = new Count(MaxTries);
        this.withSubscriber = new Count(MaxTries);
        this.withSubscriber.Retrying += this.handler;
        for (int i = 0; i < this.RecordedFailures; i++)
        {
            this.withoutSubscriber.PrepareToRetry(Failure);
            this.withSubscriber.PrepareToRetry(Failure);
        }
    }

    [Benchmark(Baseline = true)]
    public bool CanRetry() => this.withoutSubscriber.CanRetry;

    [Benchmark]
    public AggregateException Exception() => this.withoutSubscriber.Exception;

    [Benchmark]
    public void OnRetrying_WithoutSubscriber() => this.withoutSubscriber.OnRetrying(this.eventArgs);

    [Benchmark]
    public void OnRetrying_WithSubscriber() => this.withSubscriber.OnRetrying(this.eventArgs);

    [Benchmark]
    public void Retrying_SubscribeAndUnsubscribe()
    {
        this.withoutSubscriber.Retrying += this.handler;
        this.withoutSubscriber.Retrying -= this.handler;
    }

    private static void OnRetrying(object? sender, RetryEventArgs e)
    {
        // Read what a typical handler logs.
        _ = e.LastException;
        _ = e.DelayBeforeRetry;
    }
}
