// <copyright file="RetryTaskFactory.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
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
        // An action has no result, so the task returned for it carries none.
        private static readonly Func<Task, object?> NoResult = _ => null;

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, new Count(), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action with cancellation.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, cancellationToken, new Count(), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action with cancellation.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, TaskCreationOptions taskCreationOptions)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, taskCreationOptions, new Count(), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action which is passed a state object.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, state, new Count(), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action which is passed a state object.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation option.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, state, taskCreationOptions, new Count(), AnyExceptionPolicy.Default);
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
        public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, new Count(), AnyExceptionPolicy.Default);
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
            ArgumentNullException.ThrowIfNull(action);

            return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, new Count(), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action action, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

            return this.StartNew(action, taskCreationOptions, new Count(), policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
        public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
        public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(policy);

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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, strategy, AnyExceptionPolicy.Default);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, cancellationToken, strategy, AnyExceptionPolicy.Default);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, taskCreationOptions, strategy, AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state, IRetryStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, state, strategy, AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="taskCreationOptions">The task creation options.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, state, taskCreationOptions, strategy, AnyExceptionPolicy.Default);
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
        public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, strategy, AnyExceptionPolicy.Default);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);

            return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, strategy, AnyExceptionPolicy.Default);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action), () => new Task(action), NoResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, cancellationToken), () => new Task(action, cancellationToken), NoResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, Detached(taskCreationOptions)), () => new Task(action, Detached(taskCreationOptions)), NoResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
        }

        /// <summary>
        /// Start a new retriable task for an action.
        /// </summary>
        /// <param name="action">The action for which to start a retriable tasks.</param>
        /// <param name="state">The state to pass to the task on creation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the action is complete.</returns>
        public Task StartNew(Action<object?> action, object? state, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, state), () => new Task(action, state), NoResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
        public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, state, Detached(taskCreationOptions)), () => new Task(action, state, Detached(taskCreationOptions)), NoResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
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
        public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, state, cancellationToken), () => new Task(action, state, cancellationToken), NoResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
        public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, state, cancellationToken, Detached(taskCreationOptions), scheduler), () => new Task(action, state, cancellationToken, Detached(taskCreationOptions)), NoResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
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
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return Start(Task.Factory.StartNew(action, cancellationToken, Detached(taskCreationOptions), scheduler), () => new Task(action, cancellationToken, Detached(taskCreationOptions)), NoResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
        }

        /// <summary>
        /// Returns a task that completes when the first try, and any retries, of an operation have finished.
        /// </summary>
        /// <typeparam name="TResult">The type of the result of the returned task.</typeparam>
        /// <param name="firstTry">The task running the first try of the operation.</param>
        /// <param name="createTask">A function that creates an unstarted task for each retry of the operation.</param>
        /// <param name="getResult">A function that gets the result from the task of a successful try.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="taskCreationOptions">The caller's task creation options.</param>
        /// <param name="scheduler">The scheduler on which every try runs.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>
        /// A task that completes with the result of the first successful try; faults with the exceptions recorded by the
        /// strategy (or, if it recorded none, the last try's exception) when the operation will not be retried; or is
        /// cancelled, with <paramref name="cancellationToken"/>, when cancellation stops the operation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The tries must be created with <see cref="Detached(TaskCreationOptions)"/> options. If the caller asked for
        /// <see cref="TaskCreationOptions.AttachedToParent"/>, it is the returned task that attaches to the parent, so
        /// that the parent sees the outcome of the operation rather than the failures of tries that were retried.
        /// </para>
        /// <para>
        /// This is used by both the <see cref="RetryTaskFactory"/> and the <see cref="RetryTaskFactory{T}"/>.
        /// </para>
        /// </remarks>
        internal static Task<TResult> Start<TResult>(Task firstTry, Func<Task> createTask, Func<Task, TResult> getResult, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            var operation = new RetryOperation<TResult>(createTask, getResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);

            // Retry decisions and delays run on the thread pool, not the caller's scheduler, so that waiting for a
            // delay does not block (for example) a UI thread. Each retry itself runs on the caller's scheduler.
            // The operation is passed as the continuation's state, so the continuation needs no closure.
            firstTry.ContinueWith(
                static (t, state) => ((RetryOperation<TResult>)state!).Complete(t),
                operation,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return operation.Task;
        }

        /// <summary>
        /// Gets the options with which to create each try of an operation.
        /// </summary>
        /// <param name="taskCreationOptions">The caller's task creation options.</param>
        /// <returns>The caller's options, without <see cref="TaskCreationOptions.AttachedToParent"/>.</returns>
        internal static TaskCreationOptions Detached(TaskCreationOptions taskCreationOptions)
        {
            return taskCreationOptions & ~TaskCreationOptions.AttachedToParent;
        }

        /// <summary>
        /// Retries a failed task for as long as the strategy and policy allow, and returns the last task run.
        /// </summary>
        /// <exception cref="OperationCanceledException">
        /// The operation would have been retried, but cancellation was requested.
        /// </exception>
        private static Task Retry(Task task, Func<Task> createTask, CancellationToken cancellationToken, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
        {
            while (task.Exception is AggregateException exception)
            {
                TimeSpan delay = strategy.PrepareToRetry(exception);

                if (!(strategy.CanRetry && policy.CanRetryAll(exception)))
                {
                    break;
                }

                // Check before raising Retrying for a retry that will not run, and stop waiting as soon as cancellation
                // is requested rather than at the end of the delay.
                cancellationToken.ThrowIfCancellationRequested();

                strategy.OnRetrying(new RetryEventArgs(exception, delay));

                if (delay != TimeSpan.Zero)
                {
                    SleepService.Instance.SleepAsync(delay).Wait(cancellationToken);
                }

                task = createTask();
                try
                {
                    // Run the retry inline if the scheduler allows it, otherwise queue it to the scheduler and wait.
                    // Unlike Wait(), this does not throw when the retry fails; its outcome is examined at the top of the
                    // loop.
                    task.RunSynchronously(scheduler);
                }
                catch (InvalidOperationException) when (task.IsCanceled)
                {
                    // The cancellation token was signalled before the retry could start, so the task is already
                    // cancelled and cannot run.
                }
            }

            return task;
        }

        /// <summary>
        /// An operation being retried by <see cref="Start{TResult}"/>, and the task that reports its outcome.
        /// </summary>
        /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
        /// <remarks>
        /// This holds everything the retry continuation needs, so that the continuation can be a static delegate.
        /// </remarks>
        private sealed class RetryOperation<TResult> : TaskCompletionSource<TResult>
        {
            private readonly Func<Task> createTask;
            private readonly Func<Task, TResult> getResult;
            private readonly CancellationToken cancellationToken;
            private readonly TaskScheduler scheduler;
            private readonly IRetryStrategy strategy;
            private readonly IRetryPolicy policy;

            public RetryOperation(Func<Task> createTask, Func<Task, TResult> getResult, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
                : base(taskCreationOptions & TaskCreationOptions.AttachedToParent)
            {
                this.createTask = createTask;
                this.getResult = getResult;
                this.cancellationToken = cancellationToken;
                this.scheduler = scheduler;
                this.strategy = strategy;
                this.policy = policy;
            }

            public void Complete(Task task)
            {
                try
                {
                    task = Retry(task, this.createTask, this.cancellationToken, this.scheduler, this.strategy, this.policy);

                    if (task.IsCanceled)
                    {
                        // A try acknowledged cancellation, or the token was cancelled before it could start.
                        this.TrySetCanceled(this.cancellationToken);
                    }
                    else if (task.Exception is AggregateException exception)
                    {
                        // Report every failure the strategy recorded. If it recorded none (for example DoNotRetry),
                        // report the task's own exception, so that the failure is not lost.
                        AggregateException recorded = this.strategy.Exception;
                        this.TrySetException(recorded != null && recorded.InnerExceptions.Count > 0 ? recorded : exception);
                    }
                    else
                    {
                        this.TrySetResult(this.getResult(task));
                    }
                }
                catch (OperationCanceledException) when (this.cancellationToken.IsCancellationRequested)
                {
                    this.TrySetCanceled(this.cancellationToken);
                }
                catch (Exception ex)
                {
                    // The strategy or policy failed.
                    this.TrySetException(ex);
                }
            }
        }
    }
}