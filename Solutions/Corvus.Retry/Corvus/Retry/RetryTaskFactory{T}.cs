// <copyright file="RetryTaskFactory{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// A task factory for retriable tasks. Use this through the <see cref="RetryTask{T}.Factory"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result of the Task.</typeparam>
    /// <remarks>
    /// This is analagous to the <see cref="TaskFactory{T}"/>, but provides tasks with built-in retry semantics.
    /// <para>
    /// There are three different ways of retrying tasks in this library.
    /// <list type="number">
    /// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
    /// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
    /// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public class RetryTaskFactory<T>
    {
        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, cancellationToken, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, taskCreationOptions, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, state, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, TaskCreationOptions taskCreationOptions)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, state, taskCreationOptions, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, cancellationToken, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, taskCreationOptions, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, state, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, state, taskCreationOptions, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, cancellationToken, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, taskCreationOptions, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, state, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, state, taskCreationOptions, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function).ContinueWith(t => HandleTask(t, () => new Task<T>(function), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, cancellationToken).ContinueWith(t => HandleTask(t, () => new Task<T>(function, cancellationToken), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, taskCreationOptions).ContinueWith(t => HandleTask(t, () => new Task<T>(function, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, state).ContinueWith(t => HandleTask(t, () => new Task<T>(function, state), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, state, taskCreationOptions).ContinueWith(t => HandleTask(t, () => new Task<T>(function, state, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, state, cancellationToken).ContinueWith(t => HandleTask(t, () => new Task<T>(function, state, cancellationToken), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<object, T> function, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler).ContinueWith(t => HandleTask(t, () => new Task<T>(function, state, cancellationToken, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for a function.
        /// </summary>
        /// <param name="function">The function for which to start a retriable task.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the function is complete.</returns>
        public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task<T>.Factory.StartNew(function, cancellationToken, taskCreationOptions, scheduler).ContinueWith(t => HandleTask(t, () => new Task<T>(function, cancellationToken, taskCreationOptions), strategy, policy));
        }

        private static T HandleTask(Task<T> task, Func<Task<T>> createTask, IRetryStrategy strategy, IRetryPolicy policy)
        {
            task = (Task<T>)RetryTaskFactory.HandleRetry(task, createTask, strategy, policy);

            if (task is null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            RetryTaskFactory.HandleException(task, strategy);

            return task.Result;
        }
    }
}