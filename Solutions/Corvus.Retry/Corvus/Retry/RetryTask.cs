// <copyright file="RetryTask.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System.Threading.Tasks;
    using Corvus.Retry.Async;
    using Corvus.Retry.Policies;

    /// <summary>
    /// Provides a Retriable Task factory analagous to <see cref="Task.Factory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are three different ways of retrying tasks in this library.
    /// <list type="number">
    /// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
    /// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
    /// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class RetryTask
    {
        private static RetryTaskFactory? factory;

        /// <summary>
        /// Gets the retriable task factory.
        /// </summary>
        public static RetryTaskFactory Factory => factory ??= new RetryTaskFactory();
    }
}