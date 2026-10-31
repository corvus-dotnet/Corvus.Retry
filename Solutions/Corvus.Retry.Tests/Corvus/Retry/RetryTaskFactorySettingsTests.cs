// <copyright file="RetryTaskFactorySettingsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The settings of a <see cref="RetryTaskFactory"/> and a <see cref="RetryTaskFactory{T}"/> have documented defaults,
/// a changed copy comes from the copy constructor and an object initialiser, and each <see langword="init"/> accessor
/// and constructor checks its own value and names the property or the parameter that is not correct.
/// </summary>
[TestClass]
public class RetryTaskFactorySettingsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void TheDefaultsAreTheDocumentedValues()
    {
        var factory = new RetryTaskFactory<int>();

        Assert.IsInstanceOfType<Count>(factory.Strategy);
        Assert.AreSame(AnyExceptionPolicy.Default, factory.Policy);
        Assert.IsNull(factory.AttemptTimeout);
        Assert.IsNull(factory.TotalTimeout);
        Assert.IsNull(factory.OnRetry);
        Assert.AreSame(RetryDefaults.TimeProvider, factory.TimeProvider);
        Assert.IsFalse(factory.ContinueOnCapturedContext);
        Assert.IsNull(factory.CircuitBreaker);
        Assert.IsNull(factory.CanRetryResult);
        Assert.IsNull(factory.Fallback);
    }

    [TestMethod]
    public void TheDefaultsOfTheNonGenericFactoryAreTheDocumentedValues()
    {
        var factory = new RetryTaskFactory();

        Assert.IsInstanceOfType<Count>(factory.Strategy);
        Assert.AreSame(AnyExceptionPolicy.Default, factory.Policy);
        Assert.IsNull(factory.AttemptTimeout);
        Assert.IsNull(factory.TotalTimeout);
        Assert.IsNull(factory.OnRetry);
        Assert.IsNull(factory.CircuitBreaker);
        Assert.AreSame(RetryDefaults.TimeProvider, factory.TimeProvider);
        Assert.IsFalse(factory.ContinueOnCapturedContext);
    }

    [TestMethod]
    public void WithoutItsOwnTimeProvider_AFactoryUsesTheTimeProviderOfRetryDefaults()
    {
        // In this assembly, RetryDefaults.TimeProvider is the forbidden time provider, unless a test that does not run
        // in parallel installs another.
        var factory = new RetryTaskFactory();
        var factoryOfT = new RetryTaskFactory<int>();
        var copy = new RetryTaskFactory(factory);

        Assert.AreSame(RetryDefaults.TimeProvider, factory.TimeProvider);
        Assert.AreSame(RetryDefaults.TimeProvider, factoryOfT.TimeProvider);
        Assert.AreSame(RetryDefaults.TimeProvider, copy.TimeProvider);
        Assert.AreNotSame(TimeProvider.System, factory.TimeProvider);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TheDefaultFactoryMakesFiveTriesWithoutDelayAndRetriesAnyException(bool generic)
    {
        // The default factory has no delay, so it creates no timer. A timer would go to the forbidden time provider.
        var op = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => attempt % 2 == 0 ? new FormatException($"Attempt {attempt} failed") : new InvalidOperationException($"Attempt {attempt} failed"));

        Exception thrown = generic
            ? await Assert.ThrowsAsync<Exception>(() => new RetryTaskFactory<string>().Run(_ => Task.FromResult(op.Invoke())).WaitAsync(WaitLimit)).ConfigureAwait(false)
            : await Assert.ThrowsAsync<Exception>(() => new RetryTaskFactory().Run(_ => Task.FromResult(op.Invoke())).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(5, op.Invocations);
        Assert.AreSame(op.LastThrown, thrown);
    }

    [TestMethod]
    public async Task TheDefaultFactoryOfRetryTaskMakesFiveTriesWithoutDelay()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var opOfT = new FlakyOperation(FlakyOperation.AlwaysFails);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RetryTask.Factory.Run(_ => Task.FromResult(op.Invoke())).WaitAsync(WaitLimit)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RetryTask<string>.Factory.Run(_ => Task.FromResult(opOfT.Invoke())).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(5, op.Invocations);
        Assert.AreEqual(5, opOfT.Invocations);
        Assert.IsInstanceOfType<Count>(RetryTask.Factory.Strategy);
        Assert.IsInstanceOfType<Count>(RetryTask<string>.Factory.Strategy);
    }

    [TestMethod]
    public void TheConstructorWithAStrategyAndAPolicyKeepsThemAndTheOtherDefaults()
    {
        var strategy = new Count(3);
        IRetryPolicy policy = RetryPolicies.For<TimeoutException>();

        var factory = new RetryTaskFactory(strategy, policy);
        var factoryOfT = new RetryTaskFactory<int>(strategy, policy);

        Assert.AreSame(strategy, factory.Strategy);
        Assert.AreSame(policy, factory.Policy);
        Assert.IsNull(factory.AttemptTimeout);
        Assert.AreSame(strategy, factoryOfT.Strategy);
        Assert.AreSame(policy, factoryOfT.Policy);
        Assert.IsNull(factoryOfT.CanRetryResult);
    }

    [TestMethod]
    public void TheCopyConstructorMakesAChangedCopyAndKeepsTheOriginal()
    {
        Func<int, bool> canRetryResult = r => r < 0;
        var original = new RetryTaskFactory<int> { Strategy = new Count(3), CanRetryResult = canRetryResult };

        var copy = new RetryTaskFactory<int>(original) { AttemptTimeout = TimeSpan.FromSeconds(1) };

        Assert.AreSame(original.Strategy, copy.Strategy);
        Assert.AreSame(canRetryResult, copy.CanRetryResult);
        Assert.AreEqual(TimeSpan.FromSeconds(1), copy.AttemptTimeout);
        Assert.IsNull(original.AttemptTimeout);
    }

    [TestMethod]
    public void TheCopyConstructorCopiesEverySettingOfTheGenericFactory()
    {
        var timeProvider = new RecordingFakeTimeProvider();
        Func<RetryEventArgs, ValueTask> onRetry = _ => ValueTask.CompletedTask;
        Func<int, bool> canRetryResult = r => r < 0;
        var breaker = new CircuitBreaker(timeProvider: timeProvider);
        var fallback = new Fallback<int>(_ => ValueTask.FromResult(0));
        var original = new RetryTaskFactory<int>(new Count(3), RetryPolicies.For<TimeoutException>())
        {
            AttemptTimeout = TimeSpan.FromSeconds(1),
            TotalTimeout = TimeSpan.FromSeconds(2),
            OnRetry = onRetry,
            CircuitBreaker = breaker,
            TimeProvider = timeProvider,
            ContinueOnCapturedContext = true,
            CanRetryResult = canRetryResult,
            Fallback = fallback,
        };

        var copy = new RetryTaskFactory<int>(original);

        Assert.AreSame(original.Strategy, copy.Strategy);
        Assert.AreSame(original.Policy, copy.Policy);
        Assert.AreEqual(TimeSpan.FromSeconds(1), copy.AttemptTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(2), copy.TotalTimeout);
        Assert.AreSame(onRetry, copy.OnRetry);
        Assert.AreSame(breaker, copy.CircuitBreaker);
        Assert.AreSame(timeProvider, copy.TimeProvider);
        Assert.IsTrue(copy.ContinueOnCapturedContext);
        Assert.AreSame(canRetryResult, copy.CanRetryResult);
        Assert.AreSame(fallback, copy.Fallback);
    }

    [TestMethod]
    public void TheCopyConstructorCopiesEverySettingOfTheNonGenericFactory()
    {
        var timeProvider = new RecordingFakeTimeProvider();
        Func<RetryEventArgs, ValueTask> onRetry = _ => ValueTask.CompletedTask;
        var breaker = new CircuitBreaker(timeProvider: timeProvider);
        var original = new RetryTaskFactory(new Count(3), RetryPolicies.For<TimeoutException>())
        {
            AttemptTimeout = TimeSpan.FromSeconds(1),
            TotalTimeout = TimeSpan.FromSeconds(2),
            OnRetry = onRetry,
            CircuitBreaker = breaker,
            TimeProvider = timeProvider,
            ContinueOnCapturedContext = true,
        };

        var copy = new RetryTaskFactory(original);

        Assert.AreSame(original.Strategy, copy.Strategy);
        Assert.AreSame(original.Policy, copy.Policy);
        Assert.AreEqual(TimeSpan.FromSeconds(1), copy.AttemptTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(2), copy.TotalTimeout);
        Assert.AreSame(onRetry, copy.OnRetry);
        Assert.AreSame(breaker, copy.CircuitBreaker);
        Assert.AreSame(timeProvider, copy.TimeProvider);
        Assert.IsTrue(copy.ContinueOnCapturedContext);
    }

    [TestMethod]
    public void TheCopyConstructorKeepsTheDefaultTimeProviderOfTheOriginal()
    {
        // The copy of a factory without its own time provider has none either, so it follows RetryDefaults.
        var copy = new RetryTaskFactory<int>(new RetryTaskFactory<int>());
        var copyOfNonGeneric = new RetryTaskFactory(new RetryTaskFactory());

        Assert.AreSame(RetryDefaults.TimeProvider, copy.TimeProvider);
        Assert.AreSame(RetryDefaults.TimeProvider, copyOfNonGeneric.TimeProvider);
    }

    [TestMethod]
    public void TheCopyConstructorsRejectANullFactory()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryTaskFactory((RetryTaskFactory)null!));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryTaskFactory<int>((RetryTaskFactory<int>)null!));

        Assert.AreEqual("other", thrown.ParamName);
        Assert.AreEqual("other", thrownOfT.ParamName);
    }

    // REQ-09
    [TestMethod]
    public async Task AReferenceFactoryWithExponentialDelaysWithJitterAndAnAttemptTimeoutUsesSettingsOnly()
    {
        RecordingFakeTimeProvider fake = new();
        var factory = new RetryTaskFactory
        {
            Strategy = RetryStrategies.Exponential(3, TimeSpan.FromMilliseconds(200), useJitter: true),
            AttemptTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = fake,
        };
        var op = new FlakyOperation(1);

        Task<string> call = factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The first delay is between 200 ms and 3 x 200 ms. The timer for the first attempt timeout comes first.
        Assert.IsFalse(call.IsCompleted);
        Assert.HasCount(2, fake.Delays);
        Assert.AreEqual(TimeSpan.FromSeconds(5), fake.Delays[0]);
        Assert.IsInRange(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(600), fake.Delays[1]);
        fake.Advance(fake.Delays[1]);

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    // REQ-09
    [TestMethod]
    public async Task AReferenceFactoryWithATotalTimeoutAConstantDelayAndAnAttemptTimeoutUsesSettingsOnly()
    {
        RecordingFakeTimeProvider fake = new();
        var factory = new RetryTaskFactory
        {
            TotalTimeout = TimeSpan.FromSeconds(30),
            Strategy = RetryStrategies.Constant(5, TimeSpan.FromSeconds(1)),
            AttemptTimeout = TimeSpan.FromSeconds(10),
            TimeProvider = fake,
        };
        var op = new FlakyOperation(1);

        Task<string> call = factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The timers: the total timeout, the first attempt timeout, and the delay.
        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1) }, fake.Delays);
        fake.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    // REQ-09
    [TestMethod]
    [DataRow(nameof(RetryTaskFactory.AttemptTimeout), "zero")]
    [DataRow(nameof(RetryTaskFactory.AttemptTimeout), "negative")]
    [DataRow(nameof(RetryTaskFactory.AttemptTimeout), "infinite")]
    [DataRow(nameof(RetryTaskFactory.AttemptTimeout), "too long")]
    [DataRow(nameof(RetryTaskFactory.TotalTimeout), "zero")]
    [DataRow(nameof(RetryTaskFactory.TotalTimeout), "negative")]
    [DataRow(nameof(RetryTaskFactory.TotalTimeout), "infinite")]
    [DataRow(nameof(RetryTaskFactory.TotalTimeout), "too long")]
    public void TheInitAccessorsRejectATimeoutThatIsOutOfRange(string property, string value)
    {
        TimeSpan timeout = value switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromSeconds(-1),
            "infinite" => Timeout.InfiniteTimeSpan,
            _ => RetryDelay.MaxSupported + TimeSpan.FromMilliseconds(1),
        };

        bool attempt = property == nameof(RetryTaskFactory.AttemptTimeout);
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => attempt ? new RetryTaskFactory { AttemptTimeout = timeout } : new RetryTaskFactory { TotalTimeout = timeout });
        ArgumentOutOfRangeException thrownOfT = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => attempt ? new RetryTaskFactory<int> { AttemptTimeout = timeout } : new RetryTaskFactory<int> { TotalTimeout = timeout });
        ArgumentOutOfRangeException thrownByCopy = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => attempt ? new RetryTaskFactory(new RetryTaskFactory()) { AttemptTimeout = timeout } : new RetryTaskFactory(new RetryTaskFactory()) { TotalTimeout = timeout });
        ArgumentOutOfRangeException thrownByCopyOfT = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => attempt ? new RetryTaskFactory<int>(new RetryTaskFactory<int>()) { AttemptTimeout = timeout } : new RetryTaskFactory<int>(new RetryTaskFactory<int>()) { TotalTimeout = timeout });

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
        Assert.AreEqual(property, thrownByCopy.ParamName);
        Assert.AreEqual(property, thrownByCopyOfT.ParamName);
        Assert.AreEqual(timeout, thrown.ActualValue);
        Assert.AreEqual(timeout, thrownOfT.ActualValue);
    }

    // REQ-09
    [TestMethod]
    [DataRow(nameof(RetryTaskFactory.Strategy))]
    [DataRow(nameof(RetryTaskFactory.Policy))]
    [DataRow(nameof(RetryTaskFactory.TimeProvider))]
    public void TheInitAccessorsRejectANullProperty(string property)
    {
        RetryTaskFactory WithNull() => property switch
        {
            nameof(RetryTaskFactory.Strategy) => new RetryTaskFactory { Strategy = null! },
            nameof(RetryTaskFactory.Policy) => new RetryTaskFactory { Policy = null! },
            _ => new RetryTaskFactory { TimeProvider = null! },
        };

        RetryTaskFactory<int> WithNullOfT() => property switch
        {
            nameof(RetryTaskFactory.Strategy) => new RetryTaskFactory<int> { Strategy = null! },
            nameof(RetryTaskFactory.Policy) => new RetryTaskFactory<int> { Policy = null! },
            _ => new RetryTaskFactory<int> { TimeProvider = null! },
        };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => WithNull());
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(() => WithNullOfT());

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
    }

    // REQ-09
    [TestMethod]
    [DataRow("strategy")]
    [DataRow("policy")]
    public void TheConstructorsRejectANullStrategyOrPolicy(string parameter)
    {
        IRetryStrategy strategy = parameter == "strategy" ? null! : new Count(3);
        IRetryPolicy policy = parameter == "policy" ? null! : AnyExceptionPolicy.Default;

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryTaskFactory(strategy, policy));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryTaskFactory<int>(strategy, policy));

        Assert.AreEqual(parameter, thrown.ParamName);
        Assert.AreEqual(parameter, thrownOfT.ParamName);
    }

    [TestMethod]
    public async Task TheGenericFactoryAcceptsAResultSetting()
    {
        var factory = new RetryTaskFactory<int>
        {
            Strategy = new Count(2),
            CanRetryResult = r => r < 0,
            Fallback = new Fallback<int>(_ => ValueTask.FromResult(99)),
        };
        int invocations = 0;

        int result = await factory.RunValueTask(_ => ValueTask.FromResult(Interlocked.Increment(ref invocations) == 1 ? -1 : 1)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        // The predicate rejected the first result, so the factory used the setting and tried again.
        Assert.AreEqual(1, result);
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    [DataRow("one tick")]
    [DataRow("longest")]
    public void TheInitAccessorsAcceptTheShortestAndTheLongestTimeouts(string value)
    {
        TimeSpan timeout = value == "one tick" ? TimeSpan.FromTicks(1) : RetryDelay.MaxSupported;

        var factory = new RetryTaskFactory { AttemptTimeout = timeout, TotalTimeout = timeout };
        var factoryOfT = new RetryTaskFactory<int> { AttemptTimeout = timeout, TotalTimeout = timeout };

        Assert.AreEqual(timeout, factory.AttemptTimeout);
        Assert.AreEqual(timeout, factory.TotalTimeout);
        Assert.AreEqual(timeout, factoryOfT.AttemptTimeout);
        Assert.AreEqual(timeout, factoryOfT.TotalTimeout);
    }

    [TestMethod]
    public void TheInitAccessorsAcceptNullForNoTimeout()
    {
        var factory = new RetryTaskFactory(new RetryTaskFactory { AttemptTimeout = TimeSpan.FromSeconds(1), TotalTimeout = TimeSpan.FromSeconds(1) })
        {
            AttemptTimeout = null,
            TotalTimeout = null,
        };

        Assert.IsNull(factory.AttemptTimeout);
        Assert.IsNull(factory.TotalTimeout);
    }

    [TestMethod]
    public void ARetryTaskFactoryIsSealed()
    {
        // The settings are immutable only while no subclass can add state.
        var types = new List<Type> { typeof(RetryTaskFactory), typeof(RetryTaskFactory<int>), typeof(HedgedTaskFactory), typeof(HedgedTaskFactory<int>), typeof(Fallback<int>) };

        foreach (Type type in types)
        {
            Assert.IsTrue(type.IsSealed, type.Name);
        }
    }
}
