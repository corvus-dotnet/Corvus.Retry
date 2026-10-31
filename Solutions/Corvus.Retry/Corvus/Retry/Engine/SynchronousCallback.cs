// <copyright file="SynchronousCallback.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Calls an asynchronous callback from synchronous code, and blocks the thread until the callback completes.
/// </summary>
internal static class SynchronousCallback
{
    /// <summary>
    /// Calls a callback with no <see cref="SynchronizationContext"/> and with <see cref="TaskScheduler.Default"/> as the
    /// current scheduler, and waits for the task that it returns.
    /// </summary>
    /// <typeparam name="TArgs">The type of the argument of the callback.</typeparam>
    /// <param name="callback">The callback.</param>
    /// <param name="args">The argument for the callback.</param>
    /// <remarks>
    /// <para>
    /// The thread is blocked while it waits. If the callback awaits, its continuation must not need this thread. A
    /// single-threaded context, such as the context of a UI thread, would get the continuation and never run it. So
    /// this method removes the context of the thread while the callback runs, and the continuation runs on the
    /// thread pool. The method puts the context back before it returns or throws.
    /// </para>
    /// <para>
    /// With no context, an await continues on <see cref="TaskScheduler.Current"/>. A scheduler that runs one task at a
    /// time, whose task is the one that blocks here, would get the continuation and never run it either. So when a
    /// scheduler other than the default is current, the callback runs on the thread pool, where the default scheduler
    /// is current and there is no context.
    /// </para>
    /// </remarks>
    internal static void InvokeAndWait<TArgs>(Func<TArgs, ValueTask> callback, TArgs args)
    {
        if (!ReferenceEquals(TaskScheduler.Current, TaskScheduler.Default))
        {
            // Task.Run uses the default scheduler, so the continuation of the callback cannot queue to the scheduler
            // whose task waits here. GetResult rethrows the exception of the callback as it is, not in an aggregate.
            Task.Run(() => callback(args).AsTask()).GetAwaiter().GetResult();
            return;
        }

        SynchronizationContext? context = SynchronizationContext.Current;
        if (context is not null)
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        try
        {
            ValueTask task = callback(args);
            if (task.IsCompletedSuccessfully)
            {
                task.GetAwaiter().GetResult();
            }
            else
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            if (context is not null)
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }
    }
}
