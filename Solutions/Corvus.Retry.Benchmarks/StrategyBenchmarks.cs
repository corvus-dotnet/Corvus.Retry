// <copyright file="StrategyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    using System;
    using BenchmarkDotNet.Attributes;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// The cost of each built-in strategy deciding on its delays, as a retry loop drives it through a run of failures.
    /// </summary>
    /// <remarks>
    /// Strategies are stateful (they count tries and record exceptions), so each benchmark creates a new strategy and
    /// reports <see cref="Failures"/> failures to it, checking <see cref="IRetryStrategy.CanRetry"/> each time as a
    /// retry loop does. No benchmark waits for the delays it calculates.
    /// </remarks>
    [MemoryDiagnoser]
    public class StrategyBenchmarks
    {
        private const int MaxTries = 10;

        private static readonly InvalidOperationException Failure = new("Transient failure");

        /// <summary>
        /// Gets or sets the number of failures reported to each strategy.
        /// </summary>
        [Params(1, 9)]
        public int Failures { get; set; }

        [Benchmark(Baseline = true)]
        public TimeSpan Count() => this.Drive(new Count(MaxTries));

        [Benchmark]
        public TimeSpan DoNotRetry() => this.Drive(new DoNotRetry());

        [Benchmark]
        public TimeSpan Linear() => this.Drive(new Linear(TimeSpan.FromSeconds(1), MaxTries));

        [Benchmark]
        public TimeSpan Incremental() => this.Drive(new Incremental(MaxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        [Benchmark]
        public TimeSpan Backoff() => this.Drive(new Backoff(MaxTries, TimeSpan.FromSeconds(1)));

        [Benchmark]
        public TimeSpan Backoff_WithBounds()
        {
            // Narrow the default bounds, as a caller tuning the strategy would.
            var backoff = new Backoff(MaxTries, TimeSpan.FromMilliseconds(100));
            backoff.MinBackoff = backoff.DefaultMinBackoff - backoff.DeltaBackoff;
            backoff.MaxBackoff = backoff.DefaultMaxBackoff / 2;
            return this.Drive(backoff);
        }

        private TimeSpan Drive(IRetryStrategy strategy)
        {
            TimeSpan total = TimeSpan.Zero;
            for (int i = 0; i < this.Failures; i++)
            {
                total += strategy.PrepareToRetry(Failure);
                if (!strategy.CanRetry)
                {
                    break;
                }
            }

            return total;
        }
    }
}
