// <copyright file="AggregatePolicyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    using System;
    using BenchmarkDotNet.Attributes;
    using Corvus.Retry.Policies;

    /// <summary>
    /// The cost of an <see cref="AggregatePolicy"/> deciding whether to retry, as the number of child policies grows.
    /// </summary>
    /// <remarks>
    /// Every child policy allows the retry, so every child is consulted: the worst case.
    /// </remarks>
    [MemoryDiagnoser]
    public class AggregatePolicyBenchmarks
    {
        private static readonly InvalidOperationException Failure = new("Transient failure");

        private readonly AnyExceptionPolicy single = new();
        private AggregatePolicy aggregate = new();

        /// <summary>
        /// Gets or sets the number of child policies in the aggregate.
        /// </summary>
        [Params(1, 4, 16)]
        public int PolicyCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            this.aggregate = new AggregatePolicy();
            for (int i = 0; i < this.PolicyCount; i++)
            {
                this.aggregate.Policies.Add(new AnyExceptionPolicy());
            }
        }

        [Benchmark(Baseline = true)]
        public bool SinglePolicy() => this.single.CanRetry(Failure);

        [Benchmark]
        public bool Aggregate() => this.aggregate.CanRetry(Failure);
    }
}
