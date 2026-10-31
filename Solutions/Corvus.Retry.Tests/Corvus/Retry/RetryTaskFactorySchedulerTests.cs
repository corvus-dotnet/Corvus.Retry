// <copyright file="RetryTaskFactorySchedulerTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Every <c>StartNew</c> overload of <see cref="RetryTaskFactory"/> and <see cref="RetryTaskFactory{T}"/> queues every
/// try to the caller's scheduler, creates every try with the caller's options (except
/// <see cref="TaskCreationOptions.AttachedToParent"/>, which applies to the returned task only), and runs its retry
/// decisions on the thread pool.
/// </summary>
/// <remarks>
/// <para>
/// In the test process, <see cref="TaskScheduler.Current"/> is <see cref="TaskScheduler.Default"/>, so a bug that
/// uses the wrong one of the two cannot be seen from the test thread. Tests of "the caller's scheduler" therefore call
/// <c>StartNew</c> from inside a task on a <see cref="RecordingScheduler"/>, and pass that scheduler to the overloads
/// that take one.
/// </para>
/// <para>
/// Every test uses a zero-delay strategy, so none of them sleeps.
/// </para>
/// </remarks>
[TestClass]
public class RetryTaskFactorySchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly object State = new();

    public static IEnumerable<object[]> All => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.All);

    public static IEnumerable<object[]> WithPolicy => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithPolicy);

    public static IEnumerable<object[]> WithCreationOptions => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithCreationOptions);

    public static IEnumerable<object[]> WithScheduler => RetryTaskFactories.ForBoth(RetryTaskFactoryDriver.WithScheduler);

    public static IEnumerable<object[]> WithCreationOptionsAndRunContinuationsAsynchronously =>
        from object[] row in WithCreationOptions
        from (TaskCreationOptions Options, bool ExpectedInside) value in new[] { (TaskCreationOptions.None, true), (TaskCreationOptions.RunContinuationsAsynchronously, false) }
        select new object[] { row[0], row[1], row[2], value.Options, value.ExpectedInside };

    [TestMethod]
    [DynamicData(nameof(All))]
    public async Task WhenStartedFromATaskOnACustomScheduler_EveryRetryRunsOnThatScheduler(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        RecordingScheduler scheduler = new();
        FlakyOperation operation = new(2);
        List<TaskScheduler> schedulers = [];

        Task task = await RunOn(scheduler, () => StartNew(factory, overload, defaults, _ => Record(schedulers, operation), scheduler: scheduler)).ConfigureAwait(false);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(3, operation.Invocations);
        Assert.AreSequenceEqual(new[] { scheduler, scheduler, scheduler }, Snapshot(schedulers));
    }

    [TestMethod]
    [DynamicData(nameof(WithScheduler))]
    public async Task WithAScheduler_EveryTryOfEveryOverloadRunsOnIt(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        // Called from the test thread, where TaskScheduler.Current is the default, so only the argument can send the
        // tries to this scheduler.
        RecordingScheduler scheduler = new();
        FlakyOperation operation = new(2);
        List<TaskScheduler> schedulers = [];

        Task task = StartNew(factory, overload, defaults, _ => Record(schedulers, operation), scheduler: scheduler);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(3, operation.Invocations);
        Assert.AreEqual(3, scheduler.Queued);
        Assert.AreSequenceEqual(new[] { scheduler, scheduler, scheduler }, Snapshot(schedulers));
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptions))]
    public async Task WithCreationOptions_EveryTryIsCreatedWithThemExceptAttachedToParent(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        const TaskCreationOptions Options =
            TaskCreationOptions.LongRunning | TaskCreationOptions.PreferFairness | TaskCreationOptions.DenyChildAttach |
            TaskCreationOptions.HideScheduler | TaskCreationOptions.RunContinuationsAsynchronously | TaskCreationOptions.AttachedToParent;
        RecordingScheduler scheduler = new();
        FlakyOperation operation = new(2);

        Task task = await RunOn(scheduler, () => StartNew(factory, overload, defaults, _ => operation.Invoke(), options: Options, scheduler: scheduler)).ConfigureAwait(false);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        // The first task queued is the one that called StartNew. The tries follow.
        const TaskCreationOptions Expected = Options & ~TaskCreationOptions.AttachedToParent;
        Assert.AreEqual(3, operation.Invocations);
        Assert.AreSequenceEqual(new[] { Expected, Expected, Expected }, scheduler.CreationOptions.Skip(1).ToArray());
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptions))]
    public async Task WithHideScheduler_EveryTrySeesTheDefaultScheduler(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        RecordingScheduler scheduler = new();
        FlakyOperation operation = new(2);
        List<TaskScheduler> schedulers = [];

        Task task = await RunOn(scheduler, () => StartNew(factory, overload, defaults, _ => Record(schedulers, operation), options: TaskCreationOptions.HideScheduler, scheduler: scheduler)).ConfigureAwait(false);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        // Every try ran on the scheduler (it queued the caller and three tries), but none of them could see it.
        Assert.AreEqual(4, scheduler.Queued);
        Assert.AreSequenceEqual(new[] { TaskScheduler.Default, TaskScheduler.Default, TaskScheduler.Default }, Snapshot(schedulers));
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptions))]
    public async Task WithDenyChildAttach_AChildOfATryDoesNotAttach(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        using ManualResetEventSlim childGate = new();
        Task? child = null;

        try
        {
            Task task = StartNew(
                factory,
                overload,
                defaults,
                _ => child = Task.Factory.StartNew(() => childGate.Wait(Timeout * 2), CancellationToken.None, TaskCreationOptions.AttachedToParent, TaskScheduler.Default),
                options: TaskCreationOptions.DenyChildAttach);

            // If the child were attached, the try, and so the task, could not complete until the gate is set.
            await task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.IsNotNull(child);
            Assert.IsFalse(child.IsCompleted);
        }
        finally
        {
            childGate.Set();
        }

        await child.WaitAsync(Timeout).ConfigureAwait(false);
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptionsAndRunContinuationsAsynchronously))]
    public async Task WithRunContinuationsAsynchronously_EveryOverloadHonoursIt(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults, TaskCreationOptions options, bool expectedInside)
    {
        using ManualResetEventSlim gate = new();

        Task task = StartNew(factory, overload, defaults, _ => Assert.IsTrue(gate.Wait(Timeout)), options: options);

        // The try waits for the gate, so the continuation is registered before the returned task completes.
        Task<bool> ranInside = task.ContinueWith(
            static _ => new StackTrace().GetFrames().Any(f => f.GetMethod()?.DeclaringType?.FullName?.Contains("RetryOperation", StringComparison.Ordinal) == true),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        gate.Set();

        Assert.AreEqual(expectedInside, await ranInside.WaitAsync(Timeout).ConfigureAwait(false));
        Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptions))]
    public async Task WithAttachedToParent_EveryOverloadAttachesTheReturnedTaskOnly(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        using ManualResetEventSlim gate = new();
        FlakyOperation operation = new(2);

        // The first two tries fail; the third waits for the gate.
        (Task parent, Task<TaskStatus> statusWhenItsBodyHasReturned, Task<Task> started) = ParentTask.Run(
            () => StartNew(
                factory,
                overload,
                defaults,
                _ =>
                {
                    operation.Invoke();
                    Assert.IsTrue(gate.Wait(Timeout));
                },
                options: TaskCreationOptions.AttachedToParent));

        try
        {
            // The parent's body has returned, but it waits for the returned task.
            Assert.AreEqual(TaskStatus.WaitingForChildrenToComplete, await statusWhenItsBodyHasReturned.WaitAsync(Timeout).ConfigureAwait(false));
        }
        finally
        {
            gate.Set();
        }

        // The parent is not faulted by the two tries that failed: only the returned task is attached.
        await parent.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, parent.Status);
        Assert.AreEqual(TaskStatus.RanToCompletion, (await started.ConfigureAwait(false)).Status);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(WithCreationOptions))]
    public async Task WithAttachedToParent_WhenEveryTryFails_TheParentFaultsWithTheOutcomeOnly(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        (Task parent, _, Task<Task> started) = ParentTask.Run(
            () => StartNew(factory, overload, defaults, _ => operation.Invoke(), options: TaskCreationOptions.AttachedToParent));

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => parent.WaitAsync(Timeout)).ConfigureAwait(false);

        // The parent reports the returned task's failure once, not the failure of each try. (The TPL wraps the child's
        // exceptions in a new AggregateException, so it is the outcome inside it that is the same.)
        Task task = await started.ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreEqual(5, operation.Invocations);
        AggregateException outcome = Assert.IsInstanceOfType<AggregateException>(Assert.ContainsSingle(task.Exception!.InnerExceptions));
        Assert.AreSame(outcome, Assert.ContainsSingle(thrown.InnerExceptions));
        AssertSameExceptions(operation.Thrown, outcome.InnerExceptions);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WithAScheduler_TheStrategyAndPolicyRunOnTheThreadPoolNotOnThatScheduler(RetryTaskFactoryKind factory)
    {
        RecordingScheduler scheduler = new();
        FlakyOperation operation = new(2);
        List<TaskScheduler> strategySchedulers = [];
        List<TaskScheduler> policySchedulers = [];
        IRetryStrategy strategy = RetryStrategies.FromDelegate(_ =>
        {
            Record(strategySchedulers);
            return TimeSpan.Zero;
        });
        RecordingRetryPolicy policy = new(_ =>
        {
            Record(policySchedulers);
            return true;
        });

        Task task = StartNew(
            factory,
            StartNewOverload.WithCancellationTokenOptionsAndScheduler,
            StartNewDefaults.StrategyAndPolicy,
            _ => operation.Invoke(),
            strategy,
            policy,
            scheduler: scheduler);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(3, operation.Invocations);
        Assert.AreEqual(3, scheduler.Queued);
        Assert.AreSequenceEqual(new[] { TaskScheduler.Default, TaskScheduler.Default }, Snapshot(strategySchedulers));
        Assert.AreSequenceEqual(new[] { TaskScheduler.Default, TaskScheduler.Default }, Snapshot(policySchedulers));
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhenTheSchedulerIsBusyUntilTheStrategyIsAsked_TheOperationIsStillRetried(RetryTaskFactoryKind factory)
    {
        // The scheduler runs one task at a time. While the first try runs, it queues a task that holds the scheduler
        // until the strategy has been asked. If the decision were queued to the scheduler, it would wait behind that
        // task, and the strategy would not be asked until the safety bound expired.
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        using ManualResetEventSlim strategyAsked = new();
        FlakyOperation operation = new(1);
        Task<bool>? holder = null;
        IRetryStrategy strategy = RetryStrategies.FromDelegate(_ =>
        {
            strategyAsked.Set();
            return TimeSpan.Zero;
        });

        Task task = StartNew(
            factory,
            StartNewOverload.WithCancellationTokenOptionsAndScheduler,
            StartNewDefaults.StrategyAndPolicy,
            _ =>
            {
                holder ??= Task.Factory.StartNew(() => strategyAsked.Wait(Timeout), CancellationToken.None, TaskCreationOptions.None, scheduler);
                operation.Invoke();
            },
            strategy,
            new AnyExceptionPolicy(),
            scheduler: scheduler);
        await task.WaitAsync(Timeout * 2).ConfigureAwait(false);

        Assert.IsNotNull(holder);
        Assert.IsTrue(await holder.ConfigureAwait(false), "The strategy should be asked while the scheduler is busy.");
        Assert.AreEqual(2, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(All))]
    public async Task WhenTheSchedulerRefusesTheFirstTry_StartNewThrowsTaskSchedulerExceptionAndTheOperationDoesNotRun(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The first task queued is the one that calls StartNew; the second is the first try.
        RecordingScheduler scheduler = new(refuse: n => n == 2);
        FlakyOperation operation = new(0);

        TaskSchedulerException thrown = await RunOn(
            scheduler,
            () => Assert.ThrowsExactly<TaskSchedulerException>(() => StartNew(factory, overload, defaults, _ => operation.Invoke(), scheduler: scheduler))).ConfigureAwait(false);

        Assert.AreSame(scheduler.Refusal, thrown.InnerException);
        Assert.AreEqual(2, scheduler.Queued);
        Assert.AreEqual(0, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(WithPolicy))]
    public async Task WhenTheSchedulerRefusesARetry_TheRefusalIsAFailureThatTheStrategyAndPolicyDecide(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        // The tasks queued are: the caller, the first try (which fails), the second try (refused), and the third try.
        RecordingScheduler scheduler = new(refuse: n => n == 3);
        FlakyOperation operation = new(1);
        RecordingRetryPolicy policy = new(true);

        Task task = await RunOn(scheduler, () => StartNew(factory, overload, defaults, _ => operation.Invoke(), new Count(5), policy, scheduler: scheduler)).ConfigureAwait(false);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
        Assert.AreEqual(4, scheduler.Queued);
        Assert.AreEqual(2, operation.Invocations, "The refused try did not run.");
        Assert.HasCount(2, policy.Consulted);
        Assert.AreSame(operation.Thrown[0], policy.Consulted[0]);
        Assert.AreSame(scheduler.Refusal, Assert.IsInstanceOfType<TaskSchedulerException>(policy.Consulted[1]).InnerException);
    }

    [TestMethod]
    [DynamicData(nameof(WithPolicy))]
    public async Task WhenTheSchedulerRefusesARetryAndThePolicyRejectsTheRefusal_TheTaskFaultsWithEveryFailure(RetryTaskFactoryKind factory, StartNewOverload overload, StartNewDefaults defaults)
    {
        RecordingScheduler scheduler = new(refuse: n => n == 3);
        FlakyOperation operation = new(1);
        RecordingRetryPolicy policy = new(ex => ex is not TaskSchedulerException);

        Task task = await RunOn(scheduler, () => StartNew(factory, overload, defaults, _ => operation.Invoke(), new Count(5), policy, scheduler: scheduler)).ConfigureAwait(false);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, task.Status);
        Assert.AreEqual(1, operation.Invocations);
        Assert.HasCount(2, thrown.InnerExceptions);
        Assert.AreSame(operation.Thrown[0], thrown.InnerExceptions[0]);
        TaskSchedulerException refusal = Assert.IsInstanceOfType<TaskSchedulerException>(thrown.InnerExceptions[1]);
        Assert.AreSame(scheduler.Refusal, refusal.InnerException);
        Assert.AreSame(refusal, policy.Consulted[^1]);
    }

    private static Task StartNew(
        RetryTaskFactoryKind factory,
        StartNewOverload overload,
        StartNewDefaults defaults,
        Action<object?> action,
        IRetryStrategy? strategy = null,
        IRetryPolicy? policy = null,
        TaskCreationOptions options = TaskCreationOptions.None,
        TaskScheduler? scheduler = null)
    {
        return RetryTaskFactories.StartNew(factory, overload, defaults, action, State, strategy, policy, CancellationToken.None, options, scheduler);
    }

    /// <summary>
    /// Runs a function in a task on a scheduler, so that the scheduler is the function's <see cref="TaskScheduler.Current"/>.
    /// </summary>
    private static Task<T> RunOn<T>(TaskScheduler scheduler, Func<T> function)
    {
        return Task.Factory.StartNew(function, CancellationToken.None, TaskCreationOptions.None, scheduler);
    }

    private static void Record(List<TaskScheduler> schedulers, FlakyOperation? operation = null)
    {
        lock (schedulers)
        {
            schedulers.Add(TaskScheduler.Current);
        }

        operation?.Invoke();
    }

    private static TaskScheduler[] Snapshot(List<TaskScheduler> schedulers)
    {
        lock (schedulers)
        {
            return schedulers.ToArray();
        }
    }

    private static void AssertSameExceptions(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreSame(expected[i], actual[i]);
        }
    }
}
