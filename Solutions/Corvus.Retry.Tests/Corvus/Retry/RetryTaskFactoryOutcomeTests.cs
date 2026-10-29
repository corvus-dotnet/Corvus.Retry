// <copyright file="RetryTaskFactoryOutcomeTests.cs" company="Endjin Limited">
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
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryTaskFactory"/> and <see cref="RetryTaskFactory{T}"/> report the outcome of the operation: the
/// failures of the tries, the failure of the strategy or the policy, or cancellation.
/// </summary>
/// <remarks>
/// Every test uses a zero-delay strategy, so none of them sleeps.
/// </remarks>
[TestClass]
public class RetryTaskFactoryOutcomeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> WithStrategy => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithStrategy);

    public static IEnumerable<object[]> WithPolicy => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithPolicy);

    public static IEnumerable<object[]> WithState => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithState);

    public static IEnumerable<object[]> WithStrategyAndCancellationToken => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithStrategyAndCancellationToken);

    public static IEnumerable<object[]> FailureCounts =>
        from RetryTaskFactoryKind factory in Enum.GetValues<RetryTaskFactoryKind>()
        from int failures in new[] { 99, 100, 101, 150 }
        select new object[] { factory, failures };

    public static IEnumerable<object[]> Collaborators =>
        from RetryTaskFactoryKind factory in Enum.GetValues<RetryTaskFactoryKind>()
        from bool strategyThrows in new[] { true, false }
        select new object[] { factory, strategyThrows };

    [TestMethod]
    [DynamicData(nameof(WithStrategy))]
    public async Task WhenTheStrategyThrows_TheTaskFaultsWithTheExceptionOfTheStrategyAndDoesNotRetry(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The strategy's exception has the same type as the operation's, so only its identity tells them apart.
        InvalidOperationException strategyFailure = new("The strategy failed");
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        RecordingRetryPolicy policy = new(true);

        Task task = RetryTaskFactories.StartNew(factory, overload, defaults, _ => operation.Invoke(), strategy: RetryStrategies.FromDelegate(_ => throw strategyFailure), policy: policy);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreSame(strategyFailure, thrown);
        Assert.AreSame(strategyFailure, Assert.ContainsSingle(task.Exception!.InnerExceptions));
        Assert.AreEqual(1, operation.Invocations);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    [DynamicData(nameof(WithPolicy))]
    public async Task WhenThePolicyThrows_TheTaskFaultsWithTheExceptionOfThePolicyAndDoesNotRetry(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        InvalidOperationException policyFailure = new("The policy failed");
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = RetryTaskFactories.StartNew(factory, overload, defaults, _ => operation.Invoke(), policy: new RecordingRetryPolicy(_ => throw policyFailure));

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreSame(policyFailure, thrown);
        Assert.AreSame(policyFailure, Assert.ContainsSingle(task.Exception!.InnerExceptions));
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(Collaborators))]
    public async Task WhenTheStrategyOrPolicyThrowsOperationCanceledExceptionAfterCancellationIsRequested_TheTaskIsCancelled(RetryTaskFactoryKind factory, bool strategyThrows)
    {
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        IRetryStrategy strategy = RetryStrategies.FromDelegate(_ =>
        {
            if (strategyThrows)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }

            return TimeSpan.Zero;
        });
        RecordingRetryPolicy policy = new(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return true;
        });

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), strategy, policy, cancellation.Token);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Canceled, task.Status);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(Collaborators))]
    public async Task WhenTheStrategyOrPolicyThrowsOperationCanceledExceptionWithoutCancellation_TheTaskFaultsWithIt(RetryTaskFactoryKind factory, bool strategyThrows)
    {
        // The caller's token is not cancelled, so this is a failure of the strategy or policy, not cancellation.
        using CancellationTokenSource cancellation = new();
        OperationCanceledException failure = new("Cancelled by something else", null, new CancellationToken(true));
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        IRetryStrategy strategy = RetryStrategies.FromDelegate(_ => strategyThrows ? throw failure : TimeSpan.Zero);
        RecordingRetryPolicy policy = new(_ => throw failure);

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), strategy, policy, cancellation.Token);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreSame(failure, thrown);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreSame(failure, Assert.ContainsSingle(task.Exception!.InnerExceptions));
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(WithStrategyAndCancellationToken))]
    public async Task WhenTheStrategyIsExhaustedAndThenCancellationIsRequested_TheTaskFaultsWithTheFailures(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The policy would retry, but the strategy has no more tries. Cancellation stopped nothing, so the failures are
        // reported.
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(
            FlakyOperation.AlwaysFails,
            attempt =>
            {
                if (attempt == 2)
                {
                    cancellation.Cancel();
                }

                return new InvalidOperationException($"Attempt {attempt} failed");
            });

        Task task = RetryTaskFactories.StartNew(factory, overload, defaults, _ => operation.Invoke(), strategy: new Count(2), policy: new AnyExceptionPolicy(), cancellationToken: cancellation.Token);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreEqual(2, operation.Invocations);
        AssertSameExceptions(operation.Thrown, thrown.InnerExceptions);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhenThePolicyRejectsTheFailureAndThenCancellationIsRequested_TheTaskFaultsWithTheFailure(RetryTaskFactoryKind factory)
    {
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(
            FlakyOperation.AlwaysFails,
            attempt =>
            {
                cancellation.Cancel();
                return new InvalidOperationException($"Attempt {attempt} failed");
            });

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), new Count(5), new RecordingRetryPolicy(false), cancellation.Token);

        // Cancellation did not stop anything: the policy had already ruled out a retry. So the failure is reported.
        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    [DynamicData(nameof(FailureCounts))]
    public async Task WhenMoreThanOneHundredTriesFail_TheTaskFaultsWithTheFirstNinetyNineFailuresAndTheLast(RetryTaskFactoryKind factory, int failures)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        IRetryStrategy strategy = RetryStrategies.FromDelegate(a => a.AttemptNumber < failures ? TimeSpan.Zero : null);

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), strategy, new AnyExceptionPolicy());

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(failures, operation.Invocations);
        IReadOnlyList<Exception> expected = failures <= 100
            ? operation.Thrown
            : operation.Thrown.Take(99).Append(operation.Thrown[^1]).ToArray();
        Assert.HasCount(Math.Min(failures, 100), thrown.InnerExceptions);
        AssertSameExceptions(expected, thrown.InnerExceptions);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhenTheOperationThrowsAnAggregateExceptionWithTwoInnerExceptions_ItIsOneFailureAndThePolicyIsAskedAboutBoth(RetryTaskFactoryKind factory)
    {
        TimeoutException first = new();
        TimeoutException second = new();
        List<AggregateException> thrownByTheOperation = [];
        RecordingRetryPolicy policy = new(true);

        Task task = RetryTaskFactories.StartNew(
            factory,
            () =>
            {
                AggregateException failure = new(first, second);
                lock (thrownByTheOperation)
                {
                    thrownByTheOperation.Add(failure);
                }

                throw failure;
            },
            new Count(2),
            policy);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);

        // The task reports each AggregateException that the operation threw as one failure: it is not flattened.
        AssertSameExceptions(thrownByTheOperation, thrown.InnerExceptions);

        // The policy is asked about the exceptions inside it. (After the second try, the strategy has no more tries, so
        // the policy is not asked.)
        Assert.HasCount(2, thrownByTheOperation);
        AssertSameExceptions([first, second], policy.Consulted);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhenATryCancelsItselfWithAnotherToken_ItIsAFailureThatTheStrategyAndPolicyDecide(RetryTaskFactoryKind factory)
    {
        using CancellationTokenSource caller = new();
        FlakyOperation operation = new(1, _ => new OperationCanceledException(new CancellationToken(true)));
        List<RetryAttempt> attempts = [];
        IRetryStrategy strategy = RetryStrategies.FromDelegate(a =>
        {
            lock (attempts)
            {
                attempts.Add(a);
            }

            return TimeSpan.Zero;
        });
        RecordingRetryPolicy policy = new(true);

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), strategy, policy, caller.Token);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        // The strategy is given the try's AggregateException; the policy is asked about the exception inside it.
        Assert.AreEqual(2, operation.Invocations);
        RetryAttempt attempt = Assert.ContainsSingle(attempts);
        AggregateException exception = Assert.IsInstanceOfType<AggregateException>(attempt.Exception);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(exception.InnerExceptions));
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    [DynamicData(nameof(WithState))]
    public async Task ANullStateIsPassedToEveryTry(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(2);
        List<object?> statesSeen = [];

        Task task = RetryTaskFactories.StartNew(
            factory,
            overload,
            defaults,
            state =>
            {
                lock (statesSeen)
                {
                    statesSeen.Add(state);
                }

                operation.Invoke();
            },
            state: null);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.HasCount(3, statesSeen);
        Assert.IsTrue(statesSeen.All(s => s is null), "Every try should receive the null state.");
    }

    private static void AssertSameExceptions(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreSame(expected[i], actual[i], $"Failure {i}");
        }
    }
}
