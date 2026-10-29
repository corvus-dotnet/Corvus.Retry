// <copyright file="RetryTaskFactoryDelayTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public async Task WhenCancellationIsRequestedDuringTheDelay_TheTaskIsCancelledWithoutWaitingForTheDelayToEnd()
    {
        // Time does not move in this test, so the retry can only stop waiting because of cancellation.
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        using CancellationTokenSource cancellation = new();
        FlakyOperation operation = new(FlakyOperation.AlwaysFails);

        Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), cancellation.Token, new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy());

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
        Assert.IsTrue(task.IsCanceled);
        Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
        Assert.AreEqual(1, operation.Invocations);
    }

    [TestMethod]
    public async Task WhileARetryWaitsForItsDelay_TheCallersSchedulerIsFree()
    {
        RecordingFakeTimeProvider fake = TimeProviderFixture.Install(new RecordingFakeTimeProvider());
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        FlakyOperation operation = new(2);
        var stopwatch = Stopwatch.StartNew();

        Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), CancellationToken.None, TaskCreationOptions.None, scheduler, new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy());

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);

        // The scheduler runs one task at a time, so this runs only if no thread holds it during the delay.
        Task probe = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        await probe.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        fake.Advance(TimeSpan.FromHours(1));
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromHours(1));

        await task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(3, operation.Invocations);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
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
}
