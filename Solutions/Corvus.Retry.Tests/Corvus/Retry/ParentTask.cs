// <copyright file="ParentTask.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Starts a task inside a parent task, and reports the status of the parent at the moment its body has returned, so
/// that a test can see whether the started task is attached to the parent without depending on timing.
/// </summary>
/// <remarks>
/// <para>
/// The parent runs on an exclusive scheduler. Before its body returns, it queues a probe to the same scheduler. The
/// scheduler runs one task at a time, so the probe runs only after the parent's body has returned and the parent has
/// done what it does at the end of its body: it completes if no child is attached to it, and otherwise it waits for its
/// children. So the probe sees <see cref="TaskStatus.WaitingForChildrenToComplete"/> exactly when a child that has not
/// completed is attached.
/// </para>
/// <para>
/// The parent hides its scheduler, so code in its body sees <see cref="TaskScheduler.Default"/> as
/// <see cref="TaskScheduler.Current"/>, and work that it starts does not wait behind the probe.
/// </para>
/// </remarks>
internal static class ParentTask
{
    /// <summary>
    /// Starts a task inside a new parent task.
    /// </summary>
    /// <typeparam name="TTask">The type of the task that <paramref name="start"/> returns.</typeparam>
    /// <param name="start">Starts the task, in the body of the parent.</param>
    /// <returns>
    /// The parent; a task that gives the status of the parent when its body has returned; and a task that gives the
    /// task that <paramref name="start"/> returned.
    /// </returns>
    public static (Task Parent, Task<TaskStatus> StatusWhenItsBodyHasReturned, Task<TTask> Started) Run<TTask>(Func<TTask> start)
        where TTask : Task
    {
        TaskScheduler exclusive = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        TaskCompletionSource<TTask> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TaskStatus> status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task parent = null!;

        parent = new Task(
            () =>
            {
                try
                {
                    started.SetResult(start());
                }
                catch (Exception ex)
                {
                    started.SetException(ex);
                }

                Task.Factory.StartNew(() => status.SetResult(parent.Status), CancellationToken.None, TaskCreationOptions.None, exclusive);
            },
            CancellationToken.None,
            TaskCreationOptions.HideScheduler);
        parent.Start(exclusive);

        return (parent, status.Task, started.Task);
    }
}
