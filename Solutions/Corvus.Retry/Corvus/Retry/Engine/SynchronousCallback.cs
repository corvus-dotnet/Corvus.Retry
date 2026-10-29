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
    /// Calls a callback with no <see cref="SynchronizationContext"/>, and waits for the task that it returns.
    /// </summary>
    /// <typeparam name="TArgs">The type of the argument of the callback.</typeparam>
    /// <param name="callback">The callback.</param>
    /// <param name="args">The argument for the callback.</param>
    /// <remarks>
    /// The thread is blocked while it waits. If the callback awaits, its continuation must not need this thread. A
    /// single-threaded context, such as the context of a UI thread, would get the continuation and never run it. So
    /// this method removes the context of the thread while the callback runs, and the continuation runs on the
    /// thread pool. The method puts the context back before it returns or throws.
    /// </remarks>
    internal static void InvokeAndWait<TArgs>(Func<TArgs, ValueTask> callback, TArgs args)
    {
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
