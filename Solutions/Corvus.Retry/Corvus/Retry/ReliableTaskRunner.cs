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
        private readonly CancellationTokenSource cancellationTokenSource;
        private Task processingTask;

        /// <summary>
        /// In order to ensure that processingTask has been initialised, we need to invoke runFunction before returning.
        /// This is more work than we would normally do in a constructor, but as the constructor is private we think this
        /// is tolerable.
        /// </summary>
        private ReliableTaskRunner(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
        {
            this.cancellationTokenSource = new CancellationTokenSource();

            this.RunAndAttachFailureContinuation(runFunction, retryPolicy, out this.processingTask);
        }

        /// <summary>
        /// Run a cancellable function until cancellation.
        /// </summary>
        /// <param name="runFunction">The function to run until cancellation.</param>
        /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
        public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction)
        {
            if (runFunction is null)
            {
                throw new ArgumentNullException(nameof(runFunction));
            }

            return Run(runFunction, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Run a cancellable function until cancellation.
        /// </summary>
        /// <param name="runFunction">The function to run until cancellation.</param>
        /// <param name="retryPolicy">The retry policy used to control whether the task will be restarted on failure.</param>
        /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
        public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
        {
            if (runFunction is null)
            {
                throw new ArgumentNullException(nameof(runFunction));
            }

            if (retryPolicy is null)
            {
                throw new ArgumentNullException(nameof(retryPolicy));
            }

            return new ReliableTaskRunner(runFunction, retryPolicy);
        }

        /// <summary>
        /// Stops a running function.
        /// </summary>
        /// <returns>A task which completes when the function terminates.</returns>
        public Task StopAsync()
        {
            this.cancellationTokenSource.Cancel();
            return this.processingTask;
        }

        private void RunAndAttachFailureContinuation(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy, out Task task)
        {
            task = runFunction(this.cancellationTokenSource.Token);

            task.ContinueWith(
            t =>
            {
                t.Exception.Handle(_ => true);
                if (retryPolicy.CanRetry(t.Exception))
                {
                    // Run again if we were allowed to
                    this.RunAndAttachFailureContinuation(runFunction, retryPolicy, out this.processingTask);
                }
            },
            this.cancellationTokenSource.Token,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Current);
        }
    }
}