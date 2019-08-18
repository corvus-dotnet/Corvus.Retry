// <copyright file="ReliableTaskRunner.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Policies;

    /// <summary>
    /// A host for a long-running task within a process that will restart if it fails, according to a <see cref="IRetryPolicy"/>.
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
    public sealed class ReliableTaskRunner
    {
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private Task processingTask;

        private ReliableTaskRunner()
        {
        }

        /// <summary>
        /// Run a cancellable function until cancellation.
        /// </summary>
        /// <param name="runFunction">The function to run until cancellation.</param>
        /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
        public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction)
        {
            return Run(runFunction, new AnyException());
        }

        /// <summary>
        /// Run a cancellable function until cancellation.
        /// </summary>
        /// <param name="runFunction">The function to run until cancellation.</param>
        /// <param name="retryPolicy">The retry policy used to control whether the task will be restarted on failure.</param>
        /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
        public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
        {
            var runner = new ReliableTaskRunner();
            runner.RunInternal(runFunction, retryPolicy);
            return runner;
        }

        /// <summary>
        /// Stops a running function.
        /// </summary>
        /// <returns>A task which completes when the function terminates.</returns>
        public Task StopAsync()
        {
            if (this.processingTask == null)
            {
                return Task.CompletedTask;
            }

            this.cancellationTokenSource.Cancel();
            return this.processingTask;
        }

        private void RunInternal(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
        {
            this.processingTask = runFunction(this.cancellationTokenSource.Token);
            this.processingTask.ContinueWith(
            t =>
            {
                t.Exception.Handle(_ => true);
                if (retryPolicy.CanRetry(t.Exception))
                {
                    // Run again if we were allowed to
                    this.RunInternal(runFunction, retryPolicy);
                }
            },
            this.cancellationTokenSource.Token,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Current);
        }
    }
}
