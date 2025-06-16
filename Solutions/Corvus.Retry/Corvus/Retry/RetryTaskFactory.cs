// <copyright file="RetryTaskFactory.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Async;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// A task factory for retriable tasks. Use this through the <see cref="RetryTask.Factory"/>.
    /// </summary>
    /// <remarks>
    /// This is analagous to the <see cref="TaskFactory"/>, but provides tasks with built-in retry semantics.
    /// <para>
    /// There are three different ways of retrying tasks in this library.
    /// <list type="number">
    /// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
    /// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
    /// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public class RetryTaskFactory
    {
        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action with cancellation.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, cancellationToken, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action with cancellation.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, TaskCreationOptions taskCreationOptions)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, taskCreationOptions, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action which is passed a state object.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, state, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action which is passed a state object.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation option.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, TaskCreationOptions taskCreationOptions)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, state, taskCreationOptions, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action which is passed a state object.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, new Count(), new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, cancellationToken, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, taskCreationOptions, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, state, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, state, taskCreationOptions, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, cancellationToken, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, taskCreationOptions, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, state, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, state, taskCreationOptions, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, strategy, new AnyExceptionPolicy());
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            return Task.Factory.StartNew(action).ContinueWith(t => HandleTask(t, () => new Task(action), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, cancellationToken).ContinueWith(t => HandleTask(t, () => new Task(action, cancellationToken), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, taskCreationOptions).ContinueWith(t => HandleTask(t, () => new Task(action, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, state).ContinueWith(t => HandleTask(t, () => new Task(action, state), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, state, taskCreationOptions).ContinueWith(t => HandleTask(t, () => new Task(action, state, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, state, cancellationToken).ContinueWith(t => HandleTask(t, () => new Task(action, state, cancellationToken), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler).ContinueWith(t => HandleTask(t, () => new Task(action, state, cancellationToken, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="scheduler">The task scheduler.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            return Task.Factory.StartNew(action, cancellationToken, taskCreationOptions, scheduler).ContinueWith(t => HandleTask(t, () => new Task(action, cancellationToken, taskCreationOptions), strategy, policy));
        }

        /// <summary>
        /// Handles an exception in a retriable task.
        /// </summary>
        /// <param name="task">The task in which the exception has occurred.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <remarks>
        /// <para>
        /// It throws the exception currently provided by the strategy if there is both an exception on the current task, and on the strategy.
        /// </para>
        /// <para>
        /// This is used internally by both the <see cref="RetryTaskFactory"/> and the <see cref="RetryTaskFactory{T}"/> for task exception handling.
        /// </para>
        /// </remarks>
        internal static void HandleException(Task task, IRetryStrategy strategy)
        {
            if (task is null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            AggregateException exception = strategy.Exception;
            if (exception != null && task.Exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Handles retrying a given task.
        /// </summary>
        /// <param name="task">The task that is currently executing.</param>
        /// <param name="createTask">A function that can create a new task to retry the operation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A new task which is retrying the existing task, or the original task if retry is not required.</returns>
        /// <remarks>
        /// <para>
        /// If the task completed successfully, then retry is not required and the original task is returned.
        /// </para>
        /// <para>
        /// It the task failed, then this method uses the strategy to determine whether it will retry based on the exception on the original task.
        /// If it will retry, it raises the <see cref="IRetryStrategy.Retrying"/> event, then delays for the calculated time using the Sleep service.
        /// It then creates a new task using the <c>createTask</c> function provided, and runs the task synchronously.
        /// </para>
        /// </remarks>
        internal static Task HandleRetry(Task task, Func<Task> createTask, IRetryStrategy strategy, IRetryPolicy policy)
        {
            if (task is null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (createTask is null)
            {
                throw new ArgumentNullException(nameof(createTask));
            }

            if (strategy is null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            while (task.Exception != null)
            {
                TimeSpan delay = strategy.PrepareToRetry(task.Exception);

                if (!WillRetry(task, strategy, policy))
                {
                    break;
                }

                strategy.OnRetrying(new RetryEventArgs(task.Exception, delay));

                if (delay != TimeSpan.Zero)
                {
                    SleepService.Instance.Sleep(delay);
                }

                task = createTask();
                task.RunSynchronously();
            }

            return task;
        }

        private static bool WillRetry(Task task, IRetryStrategy strategy, IRetryPolicy policy)
        {
            return strategy.CanRetry && !task.IsCanceled && task.Exception != null && task.Exception.Flatten().InnerExceptions.All(policy.CanRetry);
        }

        private static void HandleTask(Task task, Func<Task> createTask, IRetryStrategy strategy, IRetryPolicy policy)
        {
            task = HandleRetry(task, createTask, strategy, policy);

            HandleException(task, strategy);
        }
    }
}