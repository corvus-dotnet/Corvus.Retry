// <copyright file="PolicyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    using System;
    using BenchmarkDotNet.Attributes;
    using Corvus.Retry.Policies;

    /// <summary>
    /// The cost of each simple built-in policy deciding whether to retry. See <see cref="AggregatePolicyBenchmarks"/>
    /// for <see cref="AggregatePolicy"/>.
    /// </summary>
    [MemoryDiagnoser]
    public class PolicyBenchmarks
    {
        private static readonly InvalidOperationException Failure = new("Transient failure");

        private readonly AnyExceptionPolicy anyException = new();
        private readonly DoNotRetryPolicy doNotRetry = new();

        [Benchmark(Baseline = true)]
        public bool AnyExceptionPolicy_CanRetry() => this.anyException.CanRetry(Failure);

        [Benchmark]
        public bool DoNotRetryPolicy_CanRetry() => this.doNotRetry.CanRetry(Failure);
    }
}
