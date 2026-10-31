// <copyright file="RecordingFakeTimeProvider.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Time;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that records every timer it creates, so that a test can wait until code on another
/// thread has started its delay before the test moves time on.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider.Advance(TimeSpan)"/> fires only the timers that exist when it is called. Code that
/// waits on another thread (the synchronous <see cref="Retriable"/> loops run with <see cref="Task.Run(Action)"/>, or
/// <see cref="RetryTask"/> tries on the thread pool) creates its timer at a time the test does not control, so the test
/// calls <see cref="WaitForTimersAsync"/> before each <see cref="FakeTimeProvider.Advance(TimeSpan)"/>.
/// </remarks>
internal sealed class RecordingFakeTimeProvider : FakeTimeProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock sync = new();
    private readonly List<TimeSpan> delays = [];

    /// <summary>
    /// Gets the number of timers created so far.
    /// </summary>
    public int TimersCreated
    {
        get
        {
            lock (this.sync)
            {
                return this.delays.Count;
            }
        }
    }

    /// <summary>
    /// Gets the due time of every timer created so far, in order.
    /// </summary>
    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (this.sync)
            {
                return this.delays.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets or sets an optional hook that the code that creates a timer calls, on its own thread, with the due time.
    /// </summary>
    public Action<TimeSpan>? OnTimerCreated { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);

        // Record only after the base class has scheduled the timer: a test that sees the count may advance at once.
        lock (this.sync)
        {
            this.delays.Add(dueTime);
        }

        this.OnTimerCreated?.Invoke(dueTime);
        return timer;
    }

    /// <summary>
    /// Waits, in real time, until at least <paramref name="count"/> timers have been created.
    /// </summary>
    /// <param name="count">The number of timers to wait for.</param>
    /// <param name="timeout">The longest time to wait. The default is 10 seconds.</param>
    /// <returns>A task that completes when the timers exist.</returns>
    public async Task WaitForTimersAsync(int count, TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? DefaultTimeout;

        while (this.TimersCreated < count)
        {
            if (stopwatch.Elapsed > limit)
            {
                Assert.Fail($"Expected {count} timers within {limit}, but {this.TimersCreated} were created.");
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }
}
