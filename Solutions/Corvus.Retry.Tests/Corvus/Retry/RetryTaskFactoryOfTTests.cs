// <copyright file="RetryTaskFactoryOfTTests.cs" company="Endjin Limited">
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
/// <see cref="RetryTaskFactory{T}"/> starts a task that runs a function, retries it according to a strategy and policy,
/// and returns the result of the successful try.
/// </summary>
/// <remarks>
/// Every test uses a zero-delay strategy (<see cref="Count"/> or <see cref="DoNotRetry"/>), so none of them sleeps.
/// </remarks>
[TestClass]
public class RetryTaskFactoryOfTTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly object State = new();

    private readonly RetryTaskFactory<string> factory = new();

    /// <summary>
    /// A kind of task that a function can return as a value of a type that is not a task type.
    /// </summary>
    public enum TaskValue
    {
        /// <summary>A <see cref="System.Threading.Tasks.Task"/>.</summary>
        Task,

        /// <summary>A <see cref="Task{TResult}"/>.</summary>
        TaskOfInt,

        /// <summary>A <see cref="System.Threading.Tasks.ValueTask"/>.</summary>
        ValueTask,

        /// <summary>A <see cref="ValueTask{TResult}"/>.</summary>
        ValueTaskOfInt,
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.All), typeof(RetryTaskFactoryDriver))]
    public async Task WhenTheFunctionFailsTwiceThenSucceeds_TheResultOfTheThirdTryIsReturned(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(2);

        string result = await this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual("attempt-3", result);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    public async Task WithAttachedToParent_TheResultOfTheSuccessfulTryIsReturnedAndTheParentIsNotFaulted()
    {
        FlakyOperation operation = new(2);
        Task<string>? retryTask = null;

        Task parent = Task.Factory.StartNew(
            () => retryTask = this.factory.StartNew(operation.Invoke, TaskCreationOptions.AttachedToParent, new Count(5), new AnyExceptionPolicy()),
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);

        await parent.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, parent.Status);
        Assert.IsNotNull(retryTask);
        Assert.AreEqual("attempt-3", retryTask.Result);
    }

    [TestMethod]
    public async Task WithAScheduler_EveryTryRunsOnThatScheduler()
    {
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        FlakyOperation operation = new(2);
        List<TaskScheduler> schedulers = new();

        string result = await this.factory.StartNew(
            () =>
            {
                lock (schedulers)
                {
                    schedulers.Add(TaskScheduler.Current);
                }

                return operation.Invoke();
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler,
            new Count(5),
            new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual("attempt-3", result);
        Assert.HasCount(3, schedulers);
        Assert.IsTrue(schedulers.All(s => s == scheduler), "Every try, not just the first, should run on the caller's scheduler.");
    }

    [TestMethod]
    public async Task WhenTheFunctionThrowsAnAggregateExceptionWithNoInnerExceptions_ThePolicyIsStillConsulted()
    {
        AggregateException failure = new("A failure with no inner exceptions");
        int invocations = 0;
        RecordingRetryPolicy policy = new(false);

        Task<string> task = this.factory.StartNew(
            () =>
            {
                Interlocked.Increment(ref invocations);
                throw failure;
            },
            new Count(5),
            policy);

        await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);

        // There is nothing to flatten, so the policy is asked about the AggregateException itself, and says no.
        Assert.AreEqual(1, invocations);
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithStrategy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenEveryTryFails_TheTaskFaultsWithAnAggregateOfEveryFailure(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task<string> task = this.StartNew(overload, defaults, operation, new Count(3), new AnyExceptionPolicy());

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(3, operation.Invocations);
        Assert.HasCount(3, thrown.InnerExceptions);
        for (int i = 0; i < 3; i++)
        {
            Assert.AreSame(operation.Thrown[i], thrown.InnerExceptions[i]);
        }
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithPolicy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenThePolicyRejectsTheFailure_TheTaskFaultsAfterASingleInvocation(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        RecordingRetryPolicy policy = new(false);

        Task<string> task = this.StartNew(overload, defaults, operation, new Count(5), policy);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(1, operation.Invocations);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(policy.Consulted));
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithState), typeof(RetryTaskFactoryDriver))]
    public async Task TheStateObjectIsPassedToEveryTry(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(2);
        List<object?> statesSeen = new();

        string result = await RetryTaskFactoryDriver.StartNew(
            this.factory,
            overload,
            defaults,
            state =>
            {
                lock (statesSeen)
                {
                    statesSeen.Add(state);
                }

                return operation.Invoke();
            },
            State,
            CancellationToken.None,
            new Count(5),
            new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual("attempt-3", result);
        Assert.HasCount(3, statesSeen);
        Assert.IsTrue(statesSeen.All(s => ReferenceEquals(s, State)), "Every try should receive the state object.");
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithoutStrategy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenNoStrategyIsGiven_UpToFiveTriesAreMade(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task<string> task = this.StartNew(overload, defaults, operation, strategy: null, new AnyExceptionPolicy());

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(5, operation.Invocations);
        Assert.HasCount(5, thrown.InnerExceptions);
    }

    [TestMethod]
    public async Task WhenTheStrategyIsDoNotRetry_TheTaskFaultsWithTheOriginalException()
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task<string> task = this.factory.StartNew(operation.Invoke, new DoNotRetry(), new AnyExceptionPolicy());

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(1, operation.Invocations);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
    public async Task WhenTheTokenIsAlreadyCancelled_TheFunctionNeverRunsAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(0);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(false);

        Task<string> task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy(), cancellation.Token);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(0, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
    public async Task WhenCancellationIsRequestedDuringRetries_TheFunctionIsNotRunAgainAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
    {
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(
            FlakyOperation.AlwaysFails,
            attempt =>
            {
                cancellation.Cancel();
                return new InvalidOperationException($"Attempt {attempt} failed");
            });

        Task<string> task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy(), cancellation.Token);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.All), typeof(RetryTaskFactoryDriver))]
    public void WhenTIsATaskType_StartNewThrowsNotSupportedExceptionBeforeTheFunctionRuns(StartNewOverload overload, StartNewDefaults defaults)
    {
        // The task of a try would complete when the function returns, before the asynchronous operation ends.
        AssertStartNewThrowsBeforeTheFunctionRuns(overload, defaults, () => Task.CompletedTask);
        AssertStartNewThrowsBeforeTheFunctionRuns(overload, defaults, () => Task.FromResult(1));
        AssertStartNewThrowsBeforeTheFunctionRuns(overload, defaults, () => ValueTask.CompletedTask);
        AssertStartNewThrowsBeforeTheFunctionRuns(overload, defaults, () => new ValueTask<int>(1));
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.All), typeof(RetryTaskFactoryDriver))]
    public async Task WhenTIsObjectAndTheFunctionReturnsATask_TheTaskFaultsWithNotSupportedExceptionAndIsNotRetried(StartNewOverload overload, StartNewDefaults defaults)
    {
        // T is not a task type, so StartNew cannot refuse the call. The returned value is checked instead.
        int invocations = 0;
        RecordingRetryPolicy policy = new(true);

        Task<object> task = RetryTaskFactoryDriver.StartNew(
            new RetryTaskFactory<object>(),
            overload,
            defaults,
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return Task.CompletedTask;
            },
            State,
            CancellationToken.None,
            new Count(5),
            policy);

        NotSupportedException thrown = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreSame(thrown, Assert.ContainsSingle(task.Exception!.InnerExceptions));
        Assert.Contains("Retriable.RetryAsync", thrown.Message);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    [DataRow(TaskValue.Task)]
    [DataRow(TaskValue.TaskOfInt)]
    [DataRow(TaskValue.ValueTask)]
    [DataRow(TaskValue.ValueTaskOfInt)]
    public async Task WhenTheFunctionReturnsATaskAsAWiderType_TheTaskFaultsWithNotSupportedExceptionAndIsNotRetried(TaskValue value)
    {
        // Every type here is one that the value converts to: object, or an interface that the task type implements.
        await AssertFaultsWithoutRetrying<object>(() => CreateTaskValue(value)).ConfigureAwait(false);
        switch (value)
        {
            case TaskValue.Task or TaskValue.TaskOfInt:
                await AssertFaultsWithoutRetrying<IAsyncResult>(() => (IAsyncResult)CreateTaskValue(value)).ConfigureAwait(false);
                await AssertFaultsWithoutRetrying<IDisposable>(() => (IDisposable)CreateTaskValue(value)).ConfigureAwait(false);
                break;
            case TaskValue.ValueTask:
                await AssertFaultsWithoutRetrying<IEquatable<ValueTask>>(() => ValueTask.CompletedTask).ConfigureAwait(false);
                break;
            default:
                await AssertFaultsWithoutRetrying<IEquatable<ValueTask<int>>>(() => new ValueTask<int>(1)).ConfigureAwait(false);
                break;
        }

        static async Task AssertFaultsWithoutRetrying<T>(Func<T> function)
        {
            int invocations = 0;
            RecordingRetryPolicy policy = new(true);

            Task<T> task = new RetryTaskFactory<T>().StartNew(
                () =>
                {
                    Interlocked.Increment(ref invocations);
                    return function();
                },
                new Count(5),
                policy);

            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => task.WaitAsync(Timeout), typeof(T).Name).ConfigureAwait(false);
            Assert.AreEqual(1, Volatile.Read(ref invocations), typeof(T).Name);
            Assert.IsEmpty(policy.Consulted, typeof(T).Name);
        }
    }

    [TestMethod]
    public async Task WhenTIsObjectAndTheFunctionReturnsAValueThatIsNotATask_TheResultIsReturned()
    {
        object value = new();

        object result = await new RetryTaskFactory<object>().StartNew(() => value, new Count(5), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreSame(value, result);
    }

    private static object CreateTaskValue(TaskValue value) => value switch
    {
        TaskValue.Task => Task.CompletedTask,
        TaskValue.TaskOfInt => Task.FromResult(1),
        TaskValue.ValueTask => ValueTask.CompletedTask,
        _ => new ValueTask<int>(1),
    };

    private static void AssertStartNewThrowsBeforeTheFunctionRuns<T>(StartNewOverload overload, StartNewDefaults defaults, Func<T> function)
    {
        int invocations = 0;

        NotSupportedException thrown = Assert.ThrowsExactly<NotSupportedException>(
            () => RetryTaskFactoryDriver.StartNew(
                new RetryTaskFactory<T>(),
                overload,
                defaults,
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return function();
                },
                State,
                CancellationToken.None,
                new Count(5),
                new AnyExceptionPolicy()));

        Assert.AreEqual(0, Volatile.Read(ref invocations), typeof(T).Name);
        Assert.Contains("Retriable.RetryAsync", thrown.Message);
    }

    private Task<string> StartNew(
        StartNewOverload overload,
        StartNewDefaults defaults,
        FlakyOperation operation,
        IRetryStrategy? strategy,
        IRetryPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        return RetryTaskFactoryDriver.StartNew(this.factory, overload, defaults, _ => operation.Invoke(), State, cancellationToken, strategy, policy);
    }
}
