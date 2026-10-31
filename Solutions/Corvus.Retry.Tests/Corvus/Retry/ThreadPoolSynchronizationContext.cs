// <copyright file="ThreadPoolSynchronizationContext.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;

/// <summary>
/// A synchronization context that runs each posted callback on the thread pool, and is the current context while the
/// callback runs. Code that runs on the context sees it as <see cref="SynchronizationContext.Current"/>.
/// </summary>
internal sealed class ThreadPoolSynchronizationContext : SynchronizationContext
{
    /// <summary>
    /// Calls a function with this context as the current context, and then restores the previous context.
    /// </summary>
    /// <typeparam name="T">The type of the result of the function.</typeparam>
    /// <param name="function">The function.</param>
    /// <returns>The result of the function.</returns>
    public T Run<T>(Func<T> function)
    {
        SynchronizationContext? original = Current;
        SetSynchronizationContext(this);
        try
        {
            return function();
        }
        finally
        {
            SetSynchronizationContext(original);
        }
    }

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static s =>
            {
                SynchronizationContext? original = Current;
                SetSynchronizationContext(s.Context);
                try
                {
                    s.Callback(s.State);
                }
                finally
                {
                    SetSynchronizationContext(original);
                }
            },
            (Context: this, Callback: d, State: state),
            preferLocal: false);
    }

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => this;
}
