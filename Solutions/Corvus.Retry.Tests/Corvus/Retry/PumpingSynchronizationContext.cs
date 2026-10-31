// <copyright file="PumpingSynchronizationContext.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A single-threaded synchronization context, like the context of a UI thread, whose thread runs each posted callback
/// in order. A test that runs on it decides when a continuation that captured the context runs: no posted callback runs
/// until the test's code on the thread awaits.
/// </summary>
internal sealed class PumpingSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();

    // An object, not a Lock, because the pump waits with Monitor.
    private readonly object sync = new();
    private bool completed;

    private PumpingSynchronizationContext()
    {
    }

    /// <summary>
    /// Gets the managed thread ID of the thread of the context.
    /// </summary>
    public int ThreadId { get; private set; }

    /// <summary>
    /// Runs an asynchronous function on a new thread that has a new instance of this context as its current context,
    /// and runs the posted callbacks on that thread until the task of the function completes.
    /// </summary>
    /// <typeparam name="T">The type of the result of the function.</typeparam>
    /// <param name="function">The function. It receives the context.</param>
    /// <returns>A task that completes as the task of the function completes.</returns>
    /// <remarks>
    /// A callback that is posted after the task of the function completes runs on the thread pool, so that a late
    /// continuation still runs.
    /// </remarks>
    public static Task<T> RunAsync<T>(Func<PumpingSynchronizationContext, Task<T>> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new PumpingSynchronizationContext { ThreadId = Environment.CurrentManagedThreadId };
            SetSynchronizationContext(context);
            Task<T> task;
            try
            {
                task = function(context);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                return;
            }

            task.ContinueWith(static (_, s) => ((PumpingSynchronizationContext)s!).Complete(), context, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            context.Pump();
            completion.SetFromTask(task);
        })
        {
            // A thread that deadlocks must not stop the test process from ending.
            IsBackground = true,
        };

        thread.Start();
        return completion.Task;
    }

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (this.sync)
        {
            if (!this.completed)
            {
                this.queue.Enqueue((d, state));
                Monitor.PulseAll(this.sync);
                return;
            }
        }

        ThreadPool.UnsafeQueueUserWorkItem(static s => s.d(s.state), (d, state), preferLocal: false);
    }

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    private void Complete()
    {
        lock (this.sync)
        {
            this.completed = true;
            Monitor.PulseAll(this.sync);
        }
    }

    private void Pump()
    {
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (this.sync)
            {
                while (this.queue.Count == 0)
                {
                    if (this.completed)
                    {
                        return;
                    }

                    Monitor.Wait(this.sync);
                }

                item = this.queue.Dequeue();
            }

            item.Callback(item.State);
        }
    }
}
