// <copyright file="RetryTaskFactoryDelayTests.cs" company="Endjin Limited">
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
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryTaskFactory"/> and <see cref="RetryTaskFactory{T}"/> wait for the strategy's delay before each retry,
/// through <see cref="RetryDefaults.TimeProvider"/>.
/// </summary>
/// <remarks>
/// The time provider is process-wide, so these tests do not run in parallel with others.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class RetryTaskFactoryDelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    private ImmediateTimeProvider time = null!;

    [TestInitialize]
    public void Initialize() => this.time = TimeProviderFixture.Install(new ImmediateTimeProvider());

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public async Task RetriesWaitForTheStrategyDelay()
    {
        FlakyOperation operation = new(2);

        await new RetryTaskFactory().StartNew(() => operation.Invoke(), new Linear(Period, 3), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreSequenceEqual(new[] { Period, Period }, this.time.Delays);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    public async Task GenericRetriesWaitForTheStrategyDelay()
    {
        FlakyOperation operation = new(2);

        string result = await new RetryTaskFactory<string>().StartNew(operation.Invoke, new Linear(Period, 3), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreSequenceEqual(new[] { Period, Period }, this.time.Delays);
        Assert.AreEqual("attempt-3", result);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhenCancellationIsRequestedDuringTheDelay_TheTaskIsCancelledWithoutWaitingForTheDelayToEnd(RetryTaskFactoryKind factory)
    {
        // Time does not move in this test, so the retry can only stop waiting because of cancellation.
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy(), cancellation.Token);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Canceled, task.Status);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    public async Task WhileARetryWaitsForItsDelay_TheCallersSchedulerIsFree()
    {
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        FlakyOperation operation = new(2);

        Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), CancellationToken.None, TaskCreationOptions.None, scheduler, new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy());

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);

        // The scheduler runs one task at a time, so this runs only if no thread holds it during the delay. Time does
        // not move until it has run.
        Task probe = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        await probe.WaitAsync(Timeout).ConfigureAwait(false);

        fake.Advance(TimeSpan.FromHours(1));
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromHours(1));

        await task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(3, operation.Invocations);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task WhileARetryWaitsForItsDelay_NoThreadIsBlocked(RetryTaskFactoryKind factory)
    {
        // Every thread that does work for the operation runs with the execution context of the call to StartNew, so
        // this counts the threads that are inside the operation's work. Time does not move until the count is zero, so
        // a thread that blocked until the delay ended would hold the count above zero until the safety bound.
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        FlakyOperation operation = new(1);
        int threadsInside = 0;
        AsyncLocal<object?> marker = new(args =>
        {
            if (args.ThreadContextChanged && args.PreviousValue is null && args.CurrentValue is not null)
            {
                Interlocked.Increment(ref threadsInside);
            }
            else if (args.ThreadContextChanged && args.PreviousValue is not null && args.CurrentValue is null)
            {
                Interlocked.Decrement(ref threadsInside);
            }
        });

        Task task;
        marker.Value = new object();
        try
        {
            task = RetryTaskFactories.StartNew(factory, () => operation.Invoke(), new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy());
        }
        finally
        {
            marker.Value = null;
        }

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await WaitUntilAsync(() => Volatile.Read(ref threadsInside) == 0, "A thread is still inside the operation while it waits for its delay.").ConfigureAwait(false);

        Assert.AreEqual(1, operation.Invocations);
        Assert.IsFalse(task.IsCompleted);

        fake.Advance(TimeSpan.FromHours(1));
        await task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(2, operation.Invocations);
    }

    [TestMethod]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactory)]
    [DataRow(RetryTaskFactoryKind.RetryTaskFactoryOfT)]
    public async Task TheStrategyIsGivenTheAttemptNumberTheFailureAndThePreviousDelayOfEachTry(RetryTaskFactoryKind factory)
    {
        TimeSpan[] delays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)];
        FlakyOperation operation = new(3);
        List<RetryAttempt> attempts = [];
        IRetryStrategy strategy = RetryStrategies.FromDelegate(a =>
        {
            lock (attempts)
            {
                attempts.Add(a);
            }

            return delays[a.AttemptNumber - 1];
        });
        RecordingRetryPolicy policy = new(true);

        await RetryTaskFactories.StartNew(factory, () => operation.Invoke(), strategy, policy).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(4, operation.Invocations);
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, attempts.Select(a => a.AttemptNumber).ToArray());
        Assert.AreSequenceEqual(new[] { TimeSpan.Zero, delays[0], delays[1] }, attempts.Select(a => a.PreviousDelay).ToArray());
        for (int i = 0; i < 3; i++)
        {
            // The strategy is given the AggregateException of the try's task; the policy is asked about the exception
            // that the operation threw, which is inside it.
            AggregateException exception = Assert.IsInstanceOfType<AggregateException>(attempts[i].Exception);
            Assert.AreSame(operation.Thrown[i], Assert.ContainsSingle(exception.InnerExceptions));
            Assert.AreSame(operation.Thrown[i], policy.Consulted[i]);
            Assert.IsNull(attempts[i].Result);
        }
    }

    [TestMethod]
    public async Task EveryTryRunsOnTheCallersSchedulerAlsoAfterADelay()
    {
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        List<TaskScheduler> schedulers = new();
        FlakyOperation operation = new(2);

        Task<string> task = new RetryTaskFactory<string>().StartNew(
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
            new Linear(TimeSpan.FromHours(1), 3),
            new AnyExceptionPolicy());

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromHours(1));
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual("attempt-3", await task.WaitAsync(Timeout).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { scheduler, scheduler, scheduler }, schedulers);
    }

    [TestMethod]
    public async Task TheActionIsRetriedOnlyAfterTheDelay()
    {
        List<string> timeline = new();
        this.time.OnDelay = _ =>
        {
            lock (timeline)
            {
                timeline.Add("delay");
            }
        };
        FlakyOperation operation = new(2);

        await new RetryTaskFactory().StartNew(
            () =>
            {
                lock (timeline)
                {
                    timeline.Add("try");
                }

                operation.Invoke();
            },
            new Linear(Period, 3),
            new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreSequenceEqual(new[] { "try", "delay", "try", "delay", "try" }, timeline);
    }

    [TestMethod]
    public async Task WithCountStrategy_NoDelayIsRequested()
    {
        FlakyOperation operation = new(2);

        await new RetryTaskFactory().StartNew(() => operation.Invoke(), new Count(3)).WaitAsync(Timeout).ConfigureAwait(false);

        Assert.AreEqual(3, operation.Invocations);
        Assert.IsEmpty(this.time.Delays);
    }

    [TestMethod]
    public async Task WhenTheRetryIsAbandoned_NoDelayIsRequestedForTheFinalFailure()
    {
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), new Linear(Period, 2), new AnyExceptionPolicy());

        await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.AreEqual(2, operation.Invocations);
        Assert.AreEqual(Period, Assert.ContainsSingle(this.time.Delays));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        // A safety bound in real time, never the assertion: in a passing run the condition becomes true at once.
        var bound = Stopwatch.StartNew();
        while (!condition())
        {
            if (bound.Elapsed > Timeout)
            {
                Assert.Fail(message);
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }
}
