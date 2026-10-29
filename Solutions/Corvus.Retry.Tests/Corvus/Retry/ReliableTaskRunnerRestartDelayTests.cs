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
    private TimerDisposalRecorder timers = null!;

    [TestInitialize]
    public void Initialize()
    {
        this.time = new RecordingFakeTimeProvider();
        this.timers = TimeProviderFixture.Install(new TimerDisposalRecorder(this.time));
    }

    [TestCleanup]
    public void Cleanup() => TimeProviderFixture.Reset();

    [TestMethod]
    public async Task WithARestartStrategy_TheRunnerWaitsForTheDelayBeforeItRestarts()
    {
        var failure = new InvalidOperationException("First run failed");
        var policy = new RecordingRetryPolicy(true);
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(failure);
                }

                secondRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            RetryStrategies.FromDelegate(_ => RestartDelay),
            policy);

        await this.time.WaitForTimersAsync(1).ConfigureAwait(false);

        // The policy accepted the failure, and the timer has the strategy's delay.
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
        Assert.AreSequenceEqual(new[] { RestartDelay }, this.time.Delays);

        // Time moves only when the test advances it, so the restart can only come from the timer.
        this.time.Advance(RestartDelay - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        this.time.Advance(TimeSpan.FromTicks(1));
        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        Assert.AreSequenceEqual(new[] { RestartDelay }, this.time.Delays);

        Assert.AreEqual(TaskStatus.RanToCompletion, await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WithARestartDelay_WhenThePolicyRefuses_NoTimerStartsAndTheRunnerDoesNotRestart()
    {
        var failure = new InvalidOperationException("The run failed");
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(failure);
            },
            RetryStrategies.FromDelegate(_ => RestartDelay),
            policy);

        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        // StopAsync waits for the restart decision to end, so no timer can start after this.
        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));

        // The policy refused, so the runner did not wait for the strategy's delay.
        Assert.IsEmpty(this.time.Delays);

        this.time.Advance(RestartDelay);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
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
        DateTimeOffset delayStarted = this.time.GetUtcNow();

        // Time does not move, so StopAsync can only end the delay by cancelling it. The time limit of the wait is only
        // a safety bound.
        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));

        // StopAsync completed before time moved, and the delay's timer was disposed.
        Assert.AreEqual(delayStarted, this.time.GetUtcNow());
        Assert.AreEqual(1, this.timers.TimersDisposed);

        // A disposed timer does not fire, so moving time on cannot restart the run. StopAsync again waits for any
        // restart decision, so the count is final.
        this.time.Advance(RestartDelay);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.StopAsync()).ConfigureAwait(false));
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        Assert.AreEqual(TaskStatus.RanToCompletion, await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false));
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenDisposeAsyncIsCalledDuringTheRestartDelay_TheRunnerDoesNotRestart()
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
        DateTimeOffset delayStarted = this.time.GetUtcNow();

        Assert.AreEqual(TaskStatus.RanToCompletion, await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false));

        // DisposeAsync completed before time moved, and the delay's timer was disposed, so it cannot fire.
        Assert.AreEqual(delayStarted, this.time.GetUtcNow());
        Assert.AreEqual(1, this.timers.TimersDisposed);

        this.time.Advance(RestartDelay);

        // StopAsync after DisposeAsync reports how the last run ended: the first run is the last run.
        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));
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

        Assert.AreEqual(TaskStatus.RanToCompletion, await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WhenTheRestartStrategyReturnsANegativeDelay_TheRunnerRestartsAtOnceAndPreviousDelayIsZero()
    {
        TimeSpan[] previousDelays = await this.RunWithFirstDelayAsync(TimeSpan.FromSeconds(-5), expectTimer: false).ConfigureAwait(false);

        // Time did not move and no timer started, so the restart did not wait.
        Assert.IsEmpty(this.time.Delays);
        Assert.AreSequenceEqual(new[] { TimeSpan.Zero, TimeSpan.Zero }, previousDelays);
    }

    [TestMethod]
    [DataRow(long.MaxValue, 0xFFFFFFFEL * TimeSpan.TicksPerMillisecond, DisplayName = "TimeSpan.MaxValue is limited to 0xFFFFFFFE ms")]
    [DataRow((0xFFFFFFFEL * TimeSpan.TicksPerMillisecond) + 1, 0xFFFFFFFEL * TimeSpan.TicksPerMillisecond, DisplayName = "One tick more than 0xFFFFFFFE ms is limited to 0xFFFFFFFE ms")]
    [DataRow(0xFFFFFFFEL * TimeSpan.TicksPerMillisecond, 0xFFFFFFFEL * TimeSpan.TicksPerMillisecond, DisplayName = "0xFFFFFFFE ms is not changed")]
    [DataRow(-TimeSpan.TicksPerMillisecond, 0L, DisplayName = "Timeout.InfiniteTimeSpan restarts at once")]
    public async Task WhenTheRestartStrategyReturnsADelayOutsideTheTimerRange_TheRunnerLimitsIt(long delayTicks, long expectedTicks)
    {
        TimeSpan expected = TimeSpan.FromTicks(expectedTicks);

        TimeSpan[] previousDelays = await this.RunWithFirstDelayAsync(TimeSpan.FromTicks(delayTicks), expectTimer: expected > TimeSpan.Zero).ConfigureAwait(false);

        // The timer and the strategy's next PreviousDelay both have the limited delay.
        Assert.AreSequenceEqual(expected == TimeSpan.Zero ? Array.Empty<TimeSpan>() : new[] { expected }, this.time.Delays);
        Assert.AreSequenceEqual(new[] { TimeSpan.Zero, expected }, previousDelays);
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

    /// <summary>
    /// Waits for a task to finish and fails the test, rather than hanging, if it does not.
    /// </summary>
    /// <returns>How the task ended. The caller asserts it: the time limit is only a safety bound.</returns>
    private static async Task<TaskStatus> WaitAsync(Task task, string description)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);
        if (winner != task)
        {
            Assert.Fail($"Timed out after {Timeout.TotalSeconds} seconds waiting for {description}.");
        }

        return task.Status;
    }

    /// <summary>
    /// Runs an operation that always fails, with a strategy that gives <paramref name="firstDelay"/> after the first
    /// failure and refuses the second.
    /// </summary>
    /// <param name="firstDelay">The delay that the strategy gives after the first failure.</param>
    /// <param name="expectTimer">
    /// <see langword="true"/> if the runner waits for a timer before the restart, which the method then fires by
    /// moving time on by the timer's due time; <see langword="false"/> if the runner restarts at once.
    /// </param>
    /// <returns>The <see cref="RetryAttempt.PreviousDelay"/> of each attempt that the strategy received.</returns>
    private async Task<TimeSpan[]> RunWithFirstDelayAsync(TimeSpan firstDelay, bool expectTimer)
    {
        var failures = new[] { new InvalidOperationException("Run 1 failed"), new InvalidOperationException("Run 2 failed") };
        var previousDelays = new List<TimeSpan>();
        TaskCompletionSource strategyRefused = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ => FailAsynchronously(failures[Interlocked.Increment(ref invocations) - 1]),
            RetryStrategies.FromDelegate(attempt =>
            {
                lock (previousDelays)
                {
                    previousDelays.Add(attempt.PreviousDelay);
                }

                if (attempt.AttemptNumber == 1)
                {
                    return firstDelay;
                }

                strategyRefused.TrySetResult();
                return null;
            }),
            AnyExceptionPolicy.Default);

        if (expectTimer)
        {
            // Move time on by the timer's due time, so that the restart can begin.
            await this.time.WaitForTimersAsync(1).ConfigureAwait(false);
            this.time.Advance(Assert.ContainsSingle(this.time.Delays));
        }

        await WaitAsync(strategyRefused.Task, "the strategy to refuse a restart").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));
        Assert.AreSame(failures[1], await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));
        Assert.AreEqual(2, Volatile.Read(ref invocations));

        lock (previousDelays)
        {
            return [.. previousDelays];
        }
    }

    /// <summary>
    /// A time provider that passes each timer to another provider, and counts the timers that the code under test
    /// disposes.
    /// </summary>
    private sealed class TimerDisposalRecorder(TimeProvider inner) : TimeProvider
    {
        private int timersDisposed;

        public int TimersDisposed => Volatile.Read(ref this.timersDisposed);

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new RecordingTimer(this, inner.CreateTimer(callback, state, dueTime, period));
        }

        private sealed class RecordingTimer(TimerDisposalRecorder owner, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                Interlocked.Increment(ref owner.timersDisposed);
                timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.timersDisposed);
                return timer.DisposeAsync();
            }
        }
    }
}
