// <copyright file="FactoryDefaultTimeProviderTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A factory without its own time provider waits through <see cref="RetryDefaults.TimeProvider"/>: <c>Run</c>,
/// <c>RunValueTask</c> and <c>StartNew</c> of <see cref="RetryTaskFactory"/>, the shared factories of
/// <see cref="RetryTask"/>, and <see cref="HedgedTask"/>.
/// </summary>
/// <remarks>
/// These tests install a fake time provider for the process, so they must not run in parallel.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class FactoryDefaultTimeProviderTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private RecordingFakeTimeProvider time = null!;

    [TestInitialize]
    public void Initialize() => this.time = TimeProviderFixture.Install(new RecordingFakeTimeProvider());

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public async Task RunWaitsThroughTheDefaultTimeProvider()
    {
        var factory = new RetryTaskFactory { Strategy = new Linear(OneHour, 2) };
        var op = new FlakyOperation(1);

        Task<string> call = factory.Run(_ => op.InvokeAsyncReturning());

        Assert.AreSame(this.time, factory.TimeProvider);
        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { OneHour }, this.time.Delays);
        this.time.Advance(OneHour);
        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RunValueTaskOfTheGenericFactoryWaitsThroughTheDefaultTimeProvider()
    {
        var factory = new RetryTaskFactory<string> { Strategy = new Linear(OneHour, 2), TotalTimeout = OneHour * 3 };
        var op = new FlakyOperation(1);

        Task<string> call = factory.RunValueTask(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The timers: the total timeout, and the delay.
        Assert.AreSequenceEqual(new[] { OneHour * 3, OneHour }, this.time.Delays);
        this.time.Advance(OneHour);
        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartNewOfTheSharedFactoryWaitsThroughTheDefaultTimeProvider(bool generic)
    {
        var op = new FlakyOperation(1);

        Task task = generic
            ? RetryTask<string>.Factory.StartNew(op.Invoke, new Linear(OneHour, 2), AnyExceptionPolicy.Default)
            : RetryTask.Factory.StartNew(() => op.Invoke(), new Linear(OneHour, 2), AnyExceptionPolicy.Default);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { OneHour }, this.time.Delays);
        Assert.IsFalse(task.IsCompleted);
        this.time.Advance(OneHour);

        await task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public async Task HedgedTaskRunStaggersTheExtraAttemptThroughTheDefaultTimeProvider()
    {
        int invocations = 0;
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> call = HedgedTask.Run(_ => Interlocked.Increment(ref invocations) == 1 ? first.Task : Task.FromResult("attempt 2"));

        // The default stagger is 2 seconds.
        Assert.AreSequenceEqual(new[] { TimeSpan.FromSeconds(2) }, this.time.Delays);
        Assert.IsFalse(call.IsCompleted);
        this.time.Advance(TimeSpan.FromSeconds(2));

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, Volatile.Read(ref invocations));
        first.SetResult("attempt 1");
    }
}
