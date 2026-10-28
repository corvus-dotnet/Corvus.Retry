// <copyright file="RetriableOverloadBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using BenchmarkDotNet.Configs;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// The cost of every public <see cref="Retriable"/> overload when the operation succeeds at the first try: the
    /// overhead a caller pays on the happy path.
    /// </summary>
    /// <remarks>
    /// The strategy and policy are shared between invocations, which is safe because a successful first try never
    /// changes them. The delegates are cached, so their allocation is not measured.
    /// See <see cref="RetriableBenchmarks"/> for the cost of failures and retries.
    /// </remarks>
    [MemoryDiagnoser]
    [CategoriesColumn]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    public class RetriableOverloadBenchmarks
    {
        private const int Result = 42;

        private static readonly Task<int> CompletedResult = Task.FromResult(Result);
        private static readonly Func<int> Function = () => Result;
        private static readonly Action Action = () => { };
        private static readonly Func<Task<int>> AsyncFunction = () => CompletedResult;
        private static readonly Func<Task> AsyncAction = () => Task.CompletedTask;

        private readonly Count strategy = new(5);
        private readonly AnyExceptionPolicy policy = new();

        [Benchmark(Baseline = true)]
        [BenchmarkCategory("Sync")]
        public int Retry_Function() => Retriable.Retry(Function);

        [Benchmark]
        [BenchmarkCategory("Sync")]
        public int Retry_Function_Token_Strategy_Policy() => Retriable.Retry(Function, CancellationToken.None, this.strategy, this.policy);

        [Benchmark]
        [BenchmarkCategory("Sync")]
        public void Retry_Action() => Retriable.Retry(Action);

        [Benchmark]
        [BenchmarkCategory("Sync")]
        public void Retry_Action_Token() => Retriable.Retry(Action, CancellationToken.None);

        [Benchmark]
        [BenchmarkCategory("Sync")]
        public void Retry_Action_Token_Strategy_Policy() => Retriable.Retry(Action, CancellationToken.None, this.strategy, this.policy);

        [Benchmark(Baseline = true)]
        [BenchmarkCategory("Async")]
        public Task<int> RetryAsync_Function() => Retriable.RetryAsync(AsyncFunction);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task<int> RetryAsync_Function_ContinueOnCapturedContext() => Retriable.RetryAsync(AsyncFunction, false);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task<int> RetryAsync_Function_Token() => Retriable.RetryAsync(AsyncFunction, CancellationToken.None);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task<int> RetryAsync_Function_Token_Strategy_Policy() => Retriable.RetryAsync(AsyncFunction, CancellationToken.None, this.strategy, this.policy);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task<int> RetryAsync_Function_Token_Strategy_Policy_ContinueOnCapturedContext() => Retriable.RetryAsync(AsyncFunction, CancellationToken.None, this.strategy, this.policy, false);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task RetryAsync_Action() => Retriable.RetryAsync(AsyncAction);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task RetryAsync_Action_ContinueOnCapturedContext() => Retriable.RetryAsync(AsyncAction, false);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task RetryAsync_Action_Token() => Retriable.RetryAsync(AsyncAction, CancellationToken.None);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task RetryAsync_Action_Token_Strategy_Policy() => Retriable.RetryAsync(AsyncAction, CancellationToken.None, this.strategy, this.policy);

        [Benchmark]
        [BenchmarkCategory("Async")]
        public Task RetryAsync_Action_Token_Strategy_Policy_ContinueOnCapturedContext() => Retriable.RetryAsync(AsyncAction, CancellationToken.None, this.strategy, this.policy, false);
    }
}
