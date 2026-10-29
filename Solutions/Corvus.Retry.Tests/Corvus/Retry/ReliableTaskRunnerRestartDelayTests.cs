// <copyright file="ReliableTaskRunnerRestartDelayTests.cs" company="Endjin Limited">
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
/// <see cref="ReliableTaskRunner.Run(Func{CancellationToken, Task}, IRetryStrategy, IRetryPolicy)"/> waits for the
/// delay of the restart strategy before each restart, through <see cref="RetryDefaults.TimeProvider"/>.
/// </summary>
/// <remarks>
/// The time provider is process-wide, so these tests do not run in parallel with others.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class ReliableTaskRunnerRestartDelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    private RecordingFakeTimeProvider time = null!;

    [TestInitialize]
    public void Initialize() => this.time = TimeProviderFixture.Install(new RecordingFakeTimeProvider());

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public async Task WithARestartStrategy_TheRunnerWaitsForTheDelayBeforeItRestarts()
    {
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(new InvalidOperationException("First run failed"));
                }

                secondRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            RetryStrategies.FromDelegate(_ => RestartDelay),
            AnyExceptionPolicy.Default);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);

        // Time moves only when the test advances it, so the restart can only come from the timer.
        this.time.Advance(RestartDelay - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        this.time.Advance(TimeSpan.FromTicks(1));
        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        Assert.AreSequenceEqual(new[] { RestartDelay }, this.time.Delays);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
    }

    [TestMethod]
    public async Task WhenStopAsyncIsCalledDuringTheRestartDelay_TheRunnerDoesNotRestartAndStopAsyncReportsTheFailure()
    {
        var failure = new InvalidOperationException("The run failed");
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(failure);
            },
            RetryStrategies.FromDelegate(_ => RestartDelay),
            AnyExceptionPolicy.Default);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);

        // Time does not move, so StopAsync can only end the delay by cancelling it.
        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(failure, actual);

        // The delay was cancelled, so moving time on does not restart the run.
        this.time.Advance(RestartDelay);
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenDisposeAsyncIsCalledDuringTheRestartDelay_TheRunnerDoesNotRestart()
    {
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(new InvalidOperationException("The run failed"));
            },
            RetryStrategies.FromDelegate(_ => RestartDelay),
            AnyExceptionPolicy.Default);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);

        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);

        this.time.Advance(RestartDelay);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task TheStrategyReceivesThePreviousDelay()
    {
        TimeSpan[] delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
        var previousDelays = new List<TimeSpan>();
        TaskCompletionSource thirdRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) <= delays.Length)
                {
                    return FailAsynchronously(new InvalidOperationException("The run failed"));
                }

                thirdRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            RetryStrategies.FromDelegate(attempt =>
            {
                lock (previousDelays)
                {
                    previousDelays.Add(attempt.PreviousDelay);
                }

                return delays[attempt.AttemptNumber - 1];
            }),
            AnyExceptionPolicy.Default);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);
        this.time.Advance(delays[0]);
        await this.time.WaitForTimersAsync(2).ConfigureAwait(false);
        this.time.Advance(delays[1]);
        await WaitAsync(thirdRunStarted.Task, "the third run to start").ConfigureAwait(false);

        lock (previousDelays)
        {
            Assert.AreSequenceEqual(new[] { TimeSpan.Zero, delays[0] }, previousDelays);
        }

        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource cancelled = NewSignal();
        cancellationToken.Register(() => cancelled.TrySetResult());
        return cancelled.Task;
    }

    private static async Task FailAsynchronously(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }

    private static async Task WaitAsync(Task task, string description)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);
        if (winner != task)
        {
            Assert.Fail($"Timed out after {Timeout.TotalSeconds} seconds waiting for {description}.");
        }
    }
}
