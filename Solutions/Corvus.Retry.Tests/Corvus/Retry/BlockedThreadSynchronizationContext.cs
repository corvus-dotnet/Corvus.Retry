// <copyright file="BlockedThreadSynchronizationContext.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A single-threaded synchronization context, like the context of a UI thread, whose thread is blocked. It queues
/// each posted callback, and nothing runs the queue. A continuation that is posted to it never runs.
/// </summary>
internal sealed class BlockedThreadSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();

    /// <summary>
    /// Runs a function on a new thread that has a new instance of this context as its current context.
    /// </summary>
    /// <typeparam name="T">The type of the result of the function.</typeparam>
    /// <param name="function">The function.</param>
    /// <returns>
    /// A task that completes with the result of the function, and whether the context is still the current context
    /// after the function returns. If the function blocks forever, the task never completes, so bound it with a time
    /// limit.
    /// </returns>
    public static Task<(T Result, bool ContextIsCurrent)> RunOnBlockedThread<T>(Func<T> function)
    {
        var context = new BlockedThreadSynchronizationContext();
        var completion = new TaskCompletionSource<(T, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                T result = function();
                completion.SetResult((result, ReferenceEquals(SynchronizationContext.Current, context)));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            // A thread that deadlocks must not stop the test process from ending.
            IsBackground = true,
        };

        thread.Start();
        return completion.Task;
    }

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state) => this.queue.Enqueue((d, state));

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
}
