// <copyright file="AttemptCountTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The number of times that each entry point invokes an operation that always fails, for every strategy class and
/// every <see cref="RetryStrategies"/> factory, and for hedging. The exception that the call throws is the exception of
/// the last attempt.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the current behaviour. The strategy tests check the delays that a strategy returns; these tests
/// check that the engines turn those delays into the documented number of attempts, end to end.
/// </para>
/// <para>
/// <see cref="Retriable"/> waits through the process-wide <see cref="RetryDefaults.TimeProvider"/>, so these tests
/// install an <see cref="ImmediateTimeProvider"/> and must not run in parallel. The factories receive the same provider
/// through their <c>TimeProvider</c> property.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class AttemptCountTests
{
    private const string RetriableRetry = "Retriable.Retry";
    private const string RetriableRetryAsync = "Retriable.RetryAsync";
    private const string FactoryRun = "RetryTaskFactory.Run";
    private const string FactoryRunOfT = "RetryTaskFactory.Run<T>";
    private const string GenericFactoryRun = "RetryTaskFactory<T>.Run";
    private const string FactoryRunValueTask = "RetryTaskFactory.RunValueTask";
    private const string FactoryRunValueTaskOfT = "RetryTaskFactory.RunValueTask<T>";
    private const string GenericFactoryRunValueTask = "RetryTaskFactory<T>.RunValueTask";
    private const string HedgedRun = "HedgedTaskFactory.Run";
    private const string HedgedRunOfT = "HedgedTaskFactory.Run<T>";
    private const string GenericHedgedRun = "HedgedTaskFactory<T>.Run";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1);

    // Each strategy by name, with the number of attempts that it allows. The name is the display name of the row.
    private static readonly Dictionary<string, (Func<IRetryStrategy> Create, int Attempts)> Strategies = new()
    {
        ["new Count(1)"] = (() => new Count(1), 1),
        ["new Count(2)"] = (() => new Count(2), 2),
        ["new Count(5)"] = (() => new Count(5), 5),
        ["new Count()"] = (() => new Count(), 5),
        ["new DoNotRetry()"] = (() => new DoNotRetry(), 1),
        ["new Linear(1 ms, 1)"] = (() => new Linear(Delay, 1), 1),
        ["new Linear(1 ms, 2)"] = (() => new Linear(Delay, 2), 2),
        ["new Linear(1 ms, 4)"] = (() => new Linear(Delay, 4), 4),
        ["new Linear(1 ms, 4, jitter)"] = (() => new Linear(Delay, 4, TimeSpan.FromSeconds(1), useJitter: true), 4),
        ["new Incremental(1)"] = (() => new Incremental(1, Delay, Delay), 1),
        ["new Incremental(2)"] = (() => new Incremental(2, Delay, Delay), 2),
        ["new Incremental(4)"] = (() => new Incremental(4, Delay, Delay), 4),
        ["new Incremental()"] = (() => new Incremental(), 5),
        ["new Backoff(1)"] = (() => new Backoff(1, Delay), 1),
        ["new Backoff(2)"] = (() => new Backoff(2, Delay), 2),
        ["new Backoff(4)"] = (() => new Backoff(4, Delay), 4),
        ["new Backoff()"] = (() => new Backoff(), 5),
        ["new Exponential(1)"] = (() => new Exponential(1, Delay), 1),
        ["new Exponential(2)"] = (() => new Exponential(2, Delay), 2),
        ["new Exponential(4)"] = (() => new Exponential(4, Delay), 4),
        ["new Exponential(4, jitter)"] = (() => new Exponential(4, Delay, useJitter: true), 4),
        ["RetryStrategies.Constant(0)"] = (() => RetryStrategies.Constant(0, Delay), 1),
        ["RetryStrategies.Constant(1)"] = (() => RetryStrategies.Constant(1, Delay), 2),
        ["RetryStrategies.Constant(3)"] = (() => RetryStrategies.Constant(3, Delay), 4),
        ["RetryStrategies.Linear(0)"] = (() => RetryStrategies.Linear(0, Delay), 1),
        ["RetryStrategies.Linear(1)"] = (() => RetryStrategies.Linear(1, Delay), 2),
        ["RetryStrategies.Linear(3)"] = (() => RetryStrategies.Linear(3, Delay), 4),
        ["RetryStrategies.Exponential(0)"] = (() => RetryStrategies.Exponential(0, Delay), 1),
        ["RetryStrategies.Exponential(1)"] = (() => RetryStrategies.Exponential(1, Delay), 2),
        ["RetryStrategies.Exponential(3)"] = (() => RetryStrategies.Exponential(3, Delay), 4),
        ["RetryStrategies.Exponential(3, jitter)"] = (() => RetryStrategies.Exponential(3, Delay, useJitter: true), 4),
        ["RetryStrategies.Default"] = (() => RetryStrategies.Default, 10),
        ["RetryStrategies.FromDelegate(null after 0)"] = (() => NullAfter(0), 1),
        ["RetryStrategies.FromDelegate(null after 1)"] = (() => NullAfter(1), 2),
        ["RetryStrategies.FromDelegate(null after 3)"] = (() => NullAfter(3), 4),
        ["RetryStrategies.RetryAfter(new Count(3))"] = (() => RetryStrategies.RetryAfter(new Count(3)), 3),
        ["RetryStrategies.RetryAfter(RetryStrategies.Constant(0))"] = (() => RetryStrategies.RetryAfter(RetryStrategies.Constant(0, Delay)), 1),
        ["RetryStrategies.RetryAfter(RetryStrategies.Exponential(2))"] = (() => RetryStrategies.RetryAfter(RetryStrategies.Exponential(2, Delay)), 3),
    };

    private ImmediateTimeProvider time = null!;

    public static IEnumerable<object[]> EntryPointsAndStrategies =>
        from entryPoint in new[] { RetriableRetry, RetriableRetryAsync, FactoryRun, FactoryRunOfT, GenericFactoryRun, FactoryRunValueTask, FactoryRunValueTaskOfT, GenericFactoryRunValueTask }
        from strategy in Strategies.Keys
        select new object[] { entryPoint, strategy };

    public static IEnumerable<object[]> HedgingRows =>
        from factory in new[] { HedgedRun, HedgedRunOfT, GenericHedgedRun }
        from extraAttempts in new[] { 1, 2, 5 }
        select new object[] { factory, extraAttempts };

    [TestInitialize]
    public void Initialize() => this.time = TimeProviderFixture.Install(new ImmediateTimeProvider());

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public void TheTableCoversEveryPublicStrategyClass()
    {
        Type[] strategyClasses = [.. typeof(IRetryStrategy).Assembly.GetTypes().Where(t => t.IsPublic && t.IsClass && !t.IsAbstract && typeof(IRetryStrategy).IsAssignableFrom(t))];
        string[] covered = [.. Strategies.Keys];

        Assert.IsNotEmpty(strategyClasses);
        foreach (Type type in strategyClasses)
        {
            Assert.Contains(k => k.StartsWith($"new {type.Name}(", StringComparison.Ordinal), covered, $"No row creates a {type.Name}.");
        }
    }

    [TestMethod]
    [DynamicData(nameof(EntryPointsAndStrategies))]
    public async Task WhenTheOperationAlwaysFails_ItIsInvokedOnceForEachAttemptThatTheStrategyAllows_AndTheLastExceptionIsThrown(string entryPoint, string strategyName)
    {
        (Func<IRetryStrategy> create, int attempts) = Strategies[strategyName];
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => this.RunAsync(entryPoint, op, create()).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(attempts, op.Invocations);
        Assert.AreSame(op.LastThrown, thrown);
        Assert.HasCount(attempts, op.Thrown);
    }

    [TestMethod]
    [DynamicData(nameof(HedgingRows))]
    public async Task WithHedging_WhenEveryAttemptFails_TheOperationIsInvokedExtraAttemptsPlusOneTimes_AndTheLastExceptionIsThrown(string factory, int extraAttempts)
    {
        // An infinite stagger starts the next attempt only after a failure, so the attempts run one after another, and
        // the last attempt to start is the last to fail.
        using SemaphoreSlim ended = new(0);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var hedged = new HedgedTaskFactory(extraAttempts, Timeout.InfiniteTimeSpan) { TimeProvider = this.time };
        var hedgedOfT = new HedgedTaskFactory<string>(extraAttempts, Timeout.InfiniteTimeSpan) { TimeProvider = this.time };

        string Attempt()
        {
            try
            {
                return op.Invoke();
            }
            finally
            {
                ended.Release();
            }
        }

        Task call = factory switch
        {
            HedgedRun => hedged.Run(_ => Ignore(Attempt()).AsTask()),
            HedgedRunOfT => hedged.Run(_ => Task.FromResult(Attempt())),
            GenericHedgedRun => hedgedOfT.Run(_ => Task.FromResult(Attempt())),
            _ => throw new ArgumentOutOfRangeException(nameof(factory)),
        };

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The extraAttempts of a hedged factory counts the attempts after the first. Wait until each attempt has ended
        // before counting, so that the count is not taken while an attempt is still starting.
        int expected = extraAttempts + 1;
        for (int i = 0; i < expected; i++)
        {
            Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        }

        Assert.AreEqual(0, ended.CurrentCount);
        Assert.AreEqual(expected, op.Invocations);
        Assert.AreSame(op.LastThrown, thrown);
        Assert.IsEmpty(this.time.Delays);
    }

    private static IRetryStrategy NullAfter(int retries)
    {
        return RetryStrategies.FromDelegate(attempt => attempt.AttemptNumber <= retries ? Delay : null);
    }

    private static ValueTask Ignore(string result)
    {
        _ = result;
        return ValueTask.CompletedTask;
    }

    private Task RunAsync(string entryPoint, FlakyOperation op, IRetryStrategy strategy)
    {
        var factory = new RetryTaskFactory { Strategy = strategy, TimeProvider = this.time };
        var factoryOfT = new RetryTaskFactory<string> { Strategy = strategy, TimeProvider = this.time };
        return entryPoint switch
        {
            RetriableRetry => RetriableDriver.RetryAsync(RetryShape.Action, op.Invoke, CancellationToken.None, strategy, AnyExceptionPolicy.Default),
            RetriableRetryAsync => RetriableDriver.RetryAsync(RetryShape.AsyncFunctionWithToken, op.Invoke, CancellationToken.None, strategy, AnyExceptionPolicy.Default),
            FactoryRun => factory.Run(_ => Ignore(op.Invoke()).AsTask()),
            FactoryRunOfT => factory.Run(_ => Task.FromResult(op.Invoke())),
            GenericFactoryRun => factoryOfT.Run(_ => Task.FromResult(op.Invoke())),
            FactoryRunValueTask => factory.RunValueTask(_ => Ignore(op.Invoke())).AsTask(),
            FactoryRunValueTaskOfT => factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask(),
            GenericFactoryRunValueTask => factoryOfT.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };
    }
}
