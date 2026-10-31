// <copyright file="RetryTaskFactoryTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryTaskFactory"/> starts a task that runs an action and retries it according to a strategy and policy.
/// </summary>
/// <remarks>
/// Every test uses a zero-delay strategy (<see cref="Count"/> or <see cref="DoNotRetry"/>), so none of them sleeps.
/// </remarks>
[TestClass]
public class RetryTaskFactoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly object State = new();

    private readonly RetryTaskFactory factory = new();

    [TestMethod]
    public void TheOverloadDriverCoversEveryPublicStartNewOverload()
    {
        // Guards the [DynamicData] tests below: if an overload is added, the driver must learn about it.
        Assert.HasCount(CountPublicStartNewMethods(typeof(RetryTaskFactory)), RetryTaskFactoryDriver.AllOverloads);
        Assert.HasCount(CountPublicStartNewMethods(typeof(RetryTaskFactory<int>)), RetryTaskFactoryDriver.AllOverloads);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.All), typeof(RetryTaskFactoryDriver))]
    public async Task WhenTheActionFailsTwiceThenSucceeds_TheTaskCompletesAfterThreeInvocations(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(2);

        Task task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy());
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithStrategy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenEveryTryFails_TheTaskFaultsWithAnAggregateOfEveryFailure(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = this.StartNew(overload, defaults, operation, new Count(3), new AnyExceptionPolicy());

        // Unlike Retriable, which rethrows only the last failure, the task reports every failure in order.
        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(3, operation.Invocations);
        AssertSameExceptions(operation.Thrown, thrown.InnerExceptions);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithPolicy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenThePolicyRejectsTheFailure_TheTaskFaultsAfterASingleInvocation(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);
        RecordingRetryPolicy policy = new(false);

        Task task = this.StartNew(overload, defaults, operation, new Count(5), policy);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(1, operation.Invocations);

        // The policy sees the operation's own exception, not the task's AggregateException.
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(policy.Consulted));
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    public async Task WhenAnyInnerExceptionIsRejectedByThePolicy_TheTaskIsNotRetried()
    {
        TimeoutException acceptable = new();
        InvalidOperationException unacceptable = new();
        AggregateException failure = new(acceptable, unacceptable);
        int invocations = 0;
        RecordingRetryPolicy policy = new(ex => ex is TimeoutException);

        Task task = this.factory.StartNew(
            () =>
            {
                Interlocked.Increment(ref invocations);
                throw failure;
            },
            new Count(5),
            policy);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(1, invocations);

        // The policy is asked about each exception inside the AggregateException that the action threw, and must
        // accept them all.
        Assert.HasCount(2, policy.Consulted);
        Assert.AreSame(acceptable, policy.Consulted[0]);
        Assert.AreSame(unacceptable, policy.Consulted[1]);

        // The task reports the AggregateException that the action threw as one failure: it is not flattened.
        Assert.AreSame(failure, Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    public async Task WhenTheActionThrowsAnAggregateExceptionWithNoInnerExceptions_ThePolicyIsStillConsulted()
    {
        AggregateException failure = new("A failure with no inner exceptions");
        int invocations = 0;
        RecordingRetryPolicy policy = new(false);

        Task task = this.factory.StartNew(
            () =>
            {
                Interlocked.Increment(ref invocations);
                throw failure;
            },
            new Count(5),
            policy);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);

        // There is nothing to flatten, so the policy is asked about the AggregateException itself, and says no.
        Assert.AreEqual(1, invocations);
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));

        // The failure is not lost: the task reports that AggregateException, not an empty one.
        Assert.AreSame(failure, Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithState), typeof(RetryTaskFactoryDriver))]
    public async Task TheStateObjectIsPassedToEveryTry(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(2);
        List<object?> statesSeen = new();

        Task task = RetryTaskFactoryDriver.StartNew(
            this.factory,
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
            State,
            CancellationToken.None,
            new Count(5),
            new AnyExceptionPolicy());
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.HasCount(3, statesSeen);
        Assert.IsTrue(statesSeen.All(s => ReferenceEquals(s, State)), "Every try should receive the state object.");
    }

    [TestMethod]
    public async Task WhenTheActionSucceedsFirstTime_ItIsInvokedOnceAndThePolicyIsNotConsulted()
    {
        FlakyOperation operation = new(0);
        RecordingRetryPolicy policy = new(true);

        Task task = this.factory.StartNew(() => operation.Invoke(), new Count(5), policy);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
        Assert.AreEqual(1, operation.Invocations);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTasksShareOneStrategyInstance_EachTaskGetsAllItsTries()
    {
        // The strategy holds no state for a call, so tasks that run at the same time do not share their tries.
        Count strategy = new(3);
        FlakyOperation[] operations = [new(FlakyOperation.AlwaysFails), new(FlakyOperation.AlwaysFails), new(FlakyOperation.AlwaysFails), new(2)];

        Task[] tasks = Array.ConvertAll(operations, operation => this.factory.StartNew(() => operation.Invoke(), strategy, new AnyExceptionPolicy()));
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Timeout)).ConfigureAwait(false);

        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(TaskStatus.Faulted, tasks[i].Status);
            Assert.AreEqual(3, operations[i].Invocations);
            AssertSameExceptions(operations[i].Thrown, Assert.IsInstanceOfType<AggregateException>(tasks[i].Exception!.InnerException).InnerExceptions);
        }

        Assert.AreEqual(TaskStatus.RanToCompletion, tasks[3].Status);
        Assert.AreEqual(3, operations[3].Invocations);
    }

    [TestMethod]
    public async Task StartNewReturnsBeforeTheActionCompletes()
    {
        using ManualResetEventSlim gate = new();
        int completed = 0;
        int sawTheGateSet = 0;

        Task task = this.factory.StartNew(
            () =>
            {
                // The action can only finish once the test has set the gate, so StartNew returned before it finished.
                if (gate.Wait(Timeout))
                {
                    Interlocked.Increment(ref sawTheGateSet);
                }

                Interlocked.Increment(ref completed);
            },
            new DoNotRetry());

        Assert.IsFalse(task.IsCompleted);
        Assert.AreEqual(0, Volatile.Read(ref completed));

        gate.Set();
        await task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(1, sawTheGateSet, "The action should have waited for the gate, not for the timeout.");
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithoutStrategy), typeof(RetryTaskFactoryDriver))]
    public async Task WhenNoStrategyIsGiven_UpToFiveTriesAreMade(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = this.StartNew(overload, defaults, operation, strategy: null, new AnyExceptionPolicy());

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(5, operation.Invocations);
        Assert.HasCount(5, thrown.InnerExceptions);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithStrategyOnly), typeof(RetryTaskFactoryDriver))]
    public async Task WhenOnlyAStrategyIsGiven_AnyExceptionIsRetried(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(
            3,
            attempt => attempt switch
            {
                1 => new TimeoutException(),
                2 => new ArgumentException("Bad argument"),
                _ => new NotSupportedException(),
            });

        Task task = this.StartNew(overload, defaults, operation, new Count(5), policy: null);
        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(4, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
    public async Task WhenTheTokenIsAlreadyCancelled_TheActionNeverRunsAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
    {
        FlakyOperation operation = new(0);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(false);

        Task task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy(), cancellation.Token);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(0, operation.Invocations);
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
    public async Task WhenCancellationIsRequestedDuringRetries_TheActionIsNotRunAgainAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
    {
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(
            FlakyOperation.AlwaysFails,
            attempt =>
            {
                cancellation.Cancel();
                return new InvalidOperationException($"Attempt {attempt} failed");
            });

        Task task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy(), cancellation.Token);

        // The operation would have been retried, but cancellation stopped it, so the task is cancelled with the
        // caller's token.
        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    public async Task WithAttachedToParent_TheParentWaitsForTheOutcomeAndIsNotFaultedByTriesThatWereRetried()
    {
        using ManualResetEventSlim gate = new();
        FlakyOperation operation = new(2);

        // The first two tries fail; the third waits for the gate.
        (Task parent, Task<TaskStatus> statusWhenItsBodyHasReturned, Task<Task> started) = ParentTask.Run(
            () => this.factory.StartNew(
                () =>
                {
                    operation.Invoke();
                    Assert.IsTrue(gate.Wait(Timeout));
                },
                TaskCreationOptions.AttachedToParent,
                new Count(5),
                new AnyExceptionPolicy()));

        try
        {
            // The parent's body has returned, but the parent waits for the retried operation.
            Assert.AreEqual(TaskStatus.WaitingForChildrenToComplete, await statusWhenItsBodyHasReturned.WaitAsync(Timeout).ConfigureAwait(false));
        }
        finally
        {
            gate.Set();
        }

        // The parent saw only the successful outcome, not the failures of the tries that were retried.
        await parent.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, parent.Status);
        Assert.AreEqual(TaskStatus.RanToCompletion, (await started.ConfigureAwait(false)).Status);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    public async Task WithAttachedToParent_WhenEveryTryFails_TheParentFaultsWithTheOutcome()
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task parent = Task.Factory.StartNew(
            () => this.factory.StartNew(() => operation.Invoke(), TaskCreationOptions.AttachedToParent, new Count(3), new AnyExceptionPolicy()),
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => parent.WaitAsync(Timeout)).ConfigureAwait(false);

        // The parent reports the retried operation's failure once, not each try's.
        AggregateException outcome = Assert.IsInstanceOfType<AggregateException>(Assert.ContainsSingle(thrown.InnerExceptions));
        AssertSameExceptions(operation.Thrown, outcome.InnerExceptions);
    }

    [TestMethod]
    public async Task WithAScheduler_EveryTryRunsOnThatScheduler()
    {
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        FlakyOperation operation = new(2);
        List<TaskScheduler> schedulers = new();

        Task task = this.factory.StartNew(
            () =>
            {
                lock (schedulers)
                {
                    schedulers.Add(TaskScheduler.Current);
                }

                operation.Invoke();
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler,
            new Count(5),
            new AnyExceptionPolicy());

        await task.WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(3, operation.Invocations);
        Assert.HasCount(3, schedulers);
        Assert.IsTrue(schedulers.All(s => s == scheduler), "Every try, not just the first, should run on the caller's scheduler.");
    }

    [TestMethod]
    [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
    public async Task WhenARetryAcknowledgesCancellation_AwaitingTheTaskThrowsTaskCanceledException(StartNewOverload overload, StartNewDefaults defaults)
    {
        using CancellationTokenSource cancellation = new();
        int invocations = 0;

        Task task = RetryTaskFactoryDriver.StartNew(
            this.factory,
            overload,
            defaults,
            _ =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    throw new InvalidOperationException("Transient failure");
                }

                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            },
            State,
            cancellation.Token,
            new Count(5),
            new AnyExceptionPolicy());

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    public async Task WhenTheStrategyIsDoNotRetry_TheTaskFaultsWithTheOriginalException()
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = this.factory.StartNew(() => operation.Invoke(), new DoNotRetry(), new AnyExceptionPolicy());

        // DoNotRetry records no exceptions, so the task reports the action's own failure.
        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(1, operation.Invocations);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
    }

    [TestMethod]
    [DataRow(TaskCreationOptions.None, true)]
    [DataRow(TaskCreationOptions.RunContinuationsAsynchronously, false)]
    public async Task WithRunContinuationsAsynchronously_AContinuationOfTheReturnedTaskDoesNotRunInsideTheRetryMachinery(TaskCreationOptions options, bool expectedInside)
    {
        using ManualResetEventSlim gate = new();

        Task task = this.factory.StartNew(() => gate.Wait(Timeout), options, new DoNotRetry(), new AnyExceptionPolicy());

        // The try waits for the gate, so the continuation is registered before the returned task completes.
        Task<bool> ranInside = task.ContinueWith(
            static _ => new StackTrace().GetFrames().Any(f => f.GetMethod()?.DeclaringType?.FullName?.Contains("RetryOperation", StringComparison.Ordinal) == true),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        gate.Set();

        Assert.AreEqual(expectedInside, await ranInside.WaitAsync(Timeout).ConfigureAwait(false));
    }

    private static int CountPublicStartNewMethods(Type factoryType)
    {
        // The obsolete overloads only stop an asynchronous action from binding to StartNew. No call can reach them.
        return factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Count(m => m.Name == "StartNew" && m.GetCustomAttribute<ObsoleteAttribute>() is null);
    }

    private static void AssertSameExceptions(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreSame(expected[i], actual[i]);
        }
    }

    private Task StartNew(
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
