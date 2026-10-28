// <copyright file="RetryTaskFactoryOverloadBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// The cost of every public <c>StartNew</c> overload of <see cref="RetryTask.Factory"/> and
    /// <see cref="RetryTask{T}.Factory"/>, when the operation succeeds at the first try.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each <see cref="Case"/> is one overload, called through the test suite's <see cref="RetryTaskFactoryDriver"/>.
    /// The driver adapts the operation to the overload's delegate type, so each case includes one small closure; the
    /// differences between cases are the differences between overloads.
    /// </para>
    /// <para>
    /// Overloads that take a scheduler are passed <see cref="TaskScheduler.Default"/>. The strategy and policy are
    /// shared, which is safe because a successful first try never changes them.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class RetryTaskFactoryOverloadBenchmarks
    {
        private const int Result = 42;

        private static readonly Action<object?> Action = _ => { };
        private static readonly Func<object?, int> Function = _ => Result;

        private readonly object state = new();
        private readonly Count strategy = new(5);
        private readonly AnyExceptionPolicy policy = new();

        /// <summary>
        /// Gets or sets the overload to call.
        /// </summary>
        [ParamsSource(nameof(Cases))]
        public StartNewCase Case { get; set; } = null!;

        /// <summary>
        /// Gets every public <c>StartNew</c> overload.
        /// </summary>
        public static IEnumerable<StartNewCase> Cases =>
            RetryTaskFactoryDriver.AllOverloads.Select(o => new StartNewCase(o.Overload, o.Defaults));

        [Benchmark]
        public Task RetryTaskFactory() =>
            RetryTaskFactoryDriver.StartNew(RetryTask.Factory, this.Case.Overload, this.Case.Defaults, Action, this.state, CancellationToken.None, this.strategy, this.policy);

        [Benchmark]
        public Task<int> RetryTaskFactoryOfT() =>
            RetryTaskFactoryDriver.StartNew(RetryTask<int>.Factory, this.Case.Overload, this.Case.Defaults, Function, this.state, CancellationToken.None, this.strategy, this.policy);
    }
}
