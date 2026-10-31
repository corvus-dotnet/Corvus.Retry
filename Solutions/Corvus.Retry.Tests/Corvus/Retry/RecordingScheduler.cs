// <copyright file="RecordingScheduler.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A <see cref="TaskScheduler"/> that runs each task on the thread pool, records the creation options of every task
/// queued to it, and can refuse to queue a task.
/// </summary>
/// <remarks>
/// <para>
/// It is not <see cref="TaskScheduler.Default"/>, so a task that runs on it sees it as
/// <see cref="TaskScheduler.Current"/> (unless the task hides it), and a bug that queues work to the wrong scheduler
/// changes what a test sees.
/// </para>
/// <para>
/// A refused task is not run: <see cref="QueueTask"/> throws <see cref="Refusal"/>, and the TPL wraps it in a
/// <see cref="TaskSchedulerException"/>. The scheduler never runs a task inline, so every task it runs was queued.
/// </para>
/// </remarks>
internal sealed class RecordingScheduler : TaskScheduler
{
    private readonly Func<int, bool> refuse;
    private readonly Lock sync = new();
    private readonly List<TaskCreationOptions> creationOptions = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingScheduler"/> class.
    /// </summary>
    /// <param name="refuse">
    /// Given the number of a call to <see cref="QueueTask"/> (the first is 1), returns <see langword="true"/> to refuse
    /// that task. By default no task is refused.
    /// </param>
    public RecordingScheduler(Func<int, bool>? refuse = null)
    {
        this.refuse = refuse ?? (_ => false);
    }

    /// <summary>
    /// Gets the exception that <see cref="QueueTask"/> throws when it refuses a task.
    /// </summary>
    public InvalidOperationException Refusal { get; } = new("The scheduler refused the task.");

    /// <summary>
    /// Gets the creation options of every task queued to this scheduler, refused or not, in order.
    /// </summary>
    public IReadOnlyList<TaskCreationOptions> CreationOptions
    {
        get
        {
            lock (this.sync)
            {
                return this.creationOptions.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the number of tasks queued to this scheduler, refused or not.
    /// </summary>
    public int Queued
    {
        get
        {
            lock (this.sync)
            {
                return this.creationOptions.Count;
            }
        }
    }

    /// <inheritdoc/>
    protected override void QueueTask(Task task)
    {
        int number;
        lock (this.sync)
        {
            this.creationOptions.Add(task.CreationOptions);
            number = this.creationOptions.Count;
        }

        if (this.refuse(number))
        {
            throw this.Refusal;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Scheduler.TryExecuteTask(s.Task), (Scheduler: this, Task: task), preferLocal: false);
    }

    /// <inheritdoc/>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    /// <inheritdoc/>
    protected override IEnumerable<Task>? GetScheduledTasks() => null;
}
