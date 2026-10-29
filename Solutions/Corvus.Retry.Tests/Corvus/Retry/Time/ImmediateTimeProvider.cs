// <copyright file="ImmediateTimeProvider.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Time;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A <see cref="TimeProvider"/> that records the due time of every timer it creates, and fires each timer at once.
/// </summary>
/// <remarks>
/// Use this to assert which delays the library asks for, without waiting for them. A timer fires on the thread pool,
/// never inside <see cref="CreateTimer"/>: the caller (for example <see cref="Task.Delay(TimeSpan, TimeProvider)"/>)
/// may not have stored the timer yet when <see cref="CreateTimer"/> returns. A timer with an infinite due time never fires.
/// </remarks>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    private readonly Lock sync = new();
    private readonly List<TimeSpan> delays = [];

    /// <summary>
    /// Gets or sets an optional hook invoked with the due time each time a timer is created or changed (used to assert
    /// ordering against operation invocations).
    /// </summary>
    public Action<TimeSpan>? OnDelay { get; set; }

    /// <summary>
    /// Gets the due time of every timer created or changed so far, in order.
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

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ImmediateTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Record(TimeSpan dueTime)
    {
        lock (this.sync)
        {
            this.delays.Add(dueTime);
        }

        this.OnDelay?.Invoke(dueTime);
    }

    private sealed class ImmediateTimer(ImmediateTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Record(dueTime);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.Callback(s.State), (Callback: callback, State: state), preferLocal: false);
            }

            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
