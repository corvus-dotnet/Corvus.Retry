// <copyright file="RetryTaskFactoryStartNewSettingsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Each <c>StartNew</c> overload of a configured <see cref="RetryTaskFactory"/> or <see cref="RetryTaskFactory{T}"/>
/// uses the strategy and the policy of the factory when it does not take them, waits through the
/// <see cref="RetryTaskFactory.TimeProvider"/> of the factory, and throws an <see cref="InvalidOperationException"/>
/// before the action runs when the factory has a setting that only <c>Run</c> and <c>RunValueTask</c> can use.
/// </summary>
[TestClass]
public class RetryTaskFactoryStartNewSettingsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    // The settings that only Run and RunValueTask can use. The last two exist only on RetryTaskFactory<T>.
    private static readonly string[] NonGenericSettings =
    [
        nameof(RetryTaskFactory.AttemptTimeout),
        nameof(RetryTaskFactory.TotalTimeout),
        nameof(RetryTaskFactory.OnRetry),
        nameof(RetryTaskFactory.CircuitBreaker),
    ];

    private static readonly string[] GenericSettings =
    [
        .. NonGenericSettings,
        nameof(RetryTaskFactory<string>.CanRetryResult),
        nameof(RetryTaskFactory<string>.Fallback),
    ];

    public static IEnumerable<object[]> EveryOverloadWithEachRunOnlySetting =>
        from RetryTaskFactoryKind kind in Enum.GetValues<RetryTaskFactoryKind>()
        from (StartNewOverload Overload, StartNewDefaults Defaults) o in RetryTaskFactoryDriver.AllOverloads
        from string setting in kind == RetryTaskFactoryKind.RetryTaskFactory ? NonGenericSettings : GenericSettings
        select new object[] { kind, o.Overload, o.Defaults, setting };

    public static IEnumerable<object[]> EveryOverloadOfBothFactories => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.All);

    [TestMethod]
    [DynamicData(nameof(EveryOverloadWithEachRunOnlySetting))]
    public void WhenTheFactoryHasARunOnlySetting_StartNewThrowsBeforeTheActionRuns(RetryTaskFactoryKind kind, StartNewOverload overload, StartNewDefaults defaults, string setting)
    {
        int invocations = 0;

        InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => StartNew(kind, Configure(kind, setting), overload, defaults, _ => invocations++));

        Assert.AreEqual(
            $"StartNew cannot use the {setting} setting of this factory. Use Run or RunValueTask, or a factory without this setting.",
            thrown.Message);
        Assert.AreEqual(0, invocations);
    }

    [TestMethod]
    [DynamicData(nameof(EveryOverloadOfBothFactories))]
    public void WhenTheFactoryHasSeveralRunOnlySettings_StartNewNamesEachOfThem(RetryTaskFactoryKind kind, StartNewOverload overload, StartNewDefaults defaults)
    {
        int invocations = 0;
        string[] settings = kind == RetryTaskFactoryKind.RetryTaskFactory ? NonGenericSettings : GenericSettings;
        string expected = kind == RetryTaskFactoryKind.RetryTaskFactory
            ? "StartNew cannot use the AttemptTimeout, TotalTimeout, OnRetry and CircuitBreaker settings of this factory. Use Run or RunValueTask, or a factory without these settings."
            : "StartNew cannot use the AttemptTimeout, TotalTimeout, OnRetry, CircuitBreaker, CanRetryResult and Fallback settings of this factory. Use Run or RunValueTask, or a factory without these settings.";

        InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => StartNew(kind, Configure(kind, settings), overload, defaults, _ => invocations++));

        Assert.AreEqual(expected, thrown.Message);
        Assert.AreEqual(0, invocations);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public void WhenTheFactoryHasTwoRunOnlySettings_StartNewNamesBothWithAnd(RetryTaskFactoryKind kind)
    {
        InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => StartNew(kind, Configure(kind, nameof(RetryTaskFactory.TotalTimeout), nameof(RetryTaskFactory.CircuitBreaker)), StartNewOverload.Plain, StartNewDefaults.Neither, _ => { }));

        Assert.AreEqual(
            "StartNew cannot use the TotalTimeout and CircuitBreaker settings of this factory. Use Run or RunValueTask, or a factory without these settings.",
            thrown.Message);
    }

    [TestMethod]
    public void WhenTheFactoryHasARunOnlySetting_StartNewChecksItsArgumentsFirst()
    {
        var factory = new RetryTaskFactory { OnRetry = _ => ValueTask.CompletedTask };
        var factoryOfT = new RetryTaskFactory<string> { CanRetryResult = _ => false };

        ArgumentNullException nullAction = Assert.ThrowsExactly<ArgumentNullException>(() => factory.StartNew((Action)null!));
        ArgumentNullException nullStrategy = Assert.ThrowsExactly<ArgumentNullException>(() => factory.StartNew(() => { }, (IRetryStrategy)null!));
        ArgumentNullException nullFunction = Assert.ThrowsExactly<ArgumentNullException>(() => factoryOfT.StartNew((Func<string>)null!));
        ArgumentNullException nullPolicy = Assert.ThrowsExactly<ArgumentNullException>(() => factoryOfT.StartNew(() => "x", (IRetryPolicy)null!));

        Assert.AreEqual("action", nullAction.ParamName);
        Assert.AreEqual("strategy", nullStrategy.ParamName);
        Assert.AreEqual("function", nullFunction.ParamName);
        Assert.AreEqual("policy", nullPolicy.ParamName);
    }

    [TestMethod]
    public void WhenTheResultIsATaskAndTheFactoryHasARunOnlySetting_StartNewThrowsNotSupportedExceptionFirst()
    {
        int invocations = 0;
        var factory = new RetryTaskFactory<Task<int>> { OnRetry = _ => ValueTask.CompletedTask };

        NotSupportedException thrown = Assert.ThrowsExactly<NotSupportedException>(
            () => factory.StartNew(() =>
            {
                invocations++;
                return Task.FromResult(1);
            }));

        Assert.StartsWith("RetryTask<T> cannot retry a function that returns a task", thrown.Message);
        Assert.AreEqual(0, invocations);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WithOnlySettingsThatStartNewCanUse_StartNewRuns(RetryTaskFactoryKind kind)
    {
        // The strategy, the policy, the time provider and ContinueOnCapturedContext do not stop StartNew.
        var op = new FlakyOperation(1);
        var fake = new RecordingFakeTimeProvider();
        object factory = kind == RetryTaskFactoryKind.RetryTaskFactory
            ? new RetryTaskFactory(new Count(2), AnyExceptionPolicy.Default) { TimeProvider = fake, ContinueOnCapturedContext = true }
            : new RetryTaskFactory<string>(new Count(2), AnyExceptionPolicy.Default) { TimeProvider = fake, ContinueOnCapturedContext = true };

        await StartNew(kind, factory, StartNewOverload.Plain, StartNewDefaults.Neither, _ => op.Invoke()).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(EveryOverloadOfBothFactories))]
    public async Task StartNewWaitsThroughTheTimeProviderOfTheFactory(RetryTaskFactoryKind kind, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The overloads that take a strategy get the same strategy as the factory, so every overload has a delay of
        // one hour. Only the fake time provider of the factory can end it, and the forbidden default provider of this
        // assembly would fail the test.
        var fake = new RecordingFakeTimeProvider();
        var op = new FlakyOperation(1);
        var strategy = new Linear(OneHour, 2);
        object factory = kind == RetryTaskFactoryKind.RetryTaskFactory
            ? new RetryTaskFactory(strategy, AnyExceptionPolicy.Default) { TimeProvider = fake }
            : new RetryTaskFactory<string>(strategy, AnyExceptionPolicy.Default) { TimeProvider = fake };

        Task task = StartNew(kind, factory, overload, defaults, _ => op.Invoke(), strategy: strategy);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
        Assert.IsFalse(task.IsCompleted);
        Assert.AreEqual(1, op.Invocations);
        fake.Advance(OneHour);

        await task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(EveryOverloadOfBothFactories))]
    public async Task AnOverloadWithoutAStrategyUsesTheStrategyOfTheFactory(RetryTaskFactoryKind kind, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The factory makes 2 tries. An overload that takes a strategy gets one that makes 3.
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        object factory = kind == RetryTaskFactoryKind.RetryTaskFactory
            ? new RetryTaskFactory { Strategy = new Count(2) }
            : new RetryTaskFactory<string> { Strategy = new Count(2) };

        Task task = StartNew(kind, factory, overload, defaults, _ => op.Invoke(), strategy: new Count(3));

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(WaitLimit)).ConfigureAwait(false);
        int expected = RetryTaskFactoryDriver.TakesStrategy(defaults) ? 3 : 2;
        Assert.AreEqual(expected, op.Invocations);
        Assert.AreSequenceEqual(op.Thrown, thrown.InnerExceptions);
    }

    [TestMethod]
    [DynamicData(nameof(EveryOverloadOfBothFactories))]
    public async Task AnOverloadWithoutAPolicyUsesThePolicyOfTheFactory(RetryTaskFactoryKind kind, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The policy of the factory refuses the failure. An overload that takes a policy gets one that accepts it.
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        IRetryPolicy refuses = RetryPolicies.For<TimeoutException>();
        object factory = kind == RetryTaskFactoryKind.RetryTaskFactory
            ? new RetryTaskFactory { Strategy = new Count(3), Policy = refuses }
            : new RetryTaskFactory<string> { Strategy = new Count(3), Policy = refuses };

        Task task = StartNew(kind, factory, overload, defaults, _ => op.Invoke(), strategy: new Count(3), policy: AnyExceptionPolicy.Default);

        await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(WaitLimit)).ConfigureAwait(false);
        int expected = RetryTaskFactoryDriver.TakesPolicy(defaults) ? 3 : 1;
        Assert.AreEqual(expected, op.Invocations);
    }

    // A factory of the kind with each named setting that only Run and RunValueTask can use.
    private static object Configure(RetryTaskFactoryKind kind, params string[] settings)
    {
        TimeSpan? attemptTimeout = settings.Contains(nameof(RetryTaskFactory.AttemptTimeout)) ? OneHour : null;
        TimeSpan? totalTimeout = settings.Contains(nameof(RetryTaskFactory.TotalTimeout)) ? OneHour : null;
        Func<RetryEventArgs, ValueTask>? onRetry = settings.Contains(nameof(RetryTaskFactory.OnRetry)) ? _ => ValueTask.CompletedTask : null;
        CircuitBreaker? breaker = settings.Contains(nameof(RetryTaskFactory.CircuitBreaker)) ? new CircuitBreaker() : null;

        if (kind == RetryTaskFactoryKind.RetryTaskFactory)
        {
            return new RetryTaskFactory
            {
                AttemptTimeout = attemptTimeout,
                TotalTimeout = totalTimeout,
                OnRetry = onRetry,
                CircuitBreaker = breaker,
            };
        }

        return new RetryTaskFactory<string>
        {
            AttemptTimeout = attemptTimeout,
            TotalTimeout = totalTimeout,
            OnRetry = onRetry,
            CircuitBreaker = breaker,
            CanRetryResult = settings.Contains(nameof(RetryTaskFactory<string>.CanRetryResult)) ? _ => false : null,
            Fallback = settings.Contains(nameof(RetryTaskFactory<string>.Fallback)) ? new Fallback<string>(_ => new ValueTask<string>("fallback")) : null,
        };
    }

    // Calls one StartNew overload of the factory. The overloads that take a strategy or a policy get the ones given
    // here; by default, one try and any exception.
    private static Task StartNew(
        RetryTaskFactoryKind kind,
        object factory,
        StartNewOverload overload,
        StartNewDefaults defaults,
        Action<object?> action,
        IRetryStrategy? strategy = null,
        IRetryPolicy? policy = null)
    {
        strategy ??= new Count(1);
        policy ??= AnyExceptionPolicy.Default;

        return kind == RetryTaskFactoryKind.RetryTaskFactory
            ? RetryTaskFactoryDriver.StartNew((RetryTaskFactory)factory, overload, defaults, action, null, CancellationToken.None, strategy, policy)
            : RetryTaskFactoryDriver.StartNew(
                (RetryTaskFactory<string>)factory,
                overload,
                defaults,
                s =>
                {
                    action(s);
                    return RetryTaskFactories.Result;
                },
                null,
                CancellationToken.None,
                strategy,
                policy);
    }
}
