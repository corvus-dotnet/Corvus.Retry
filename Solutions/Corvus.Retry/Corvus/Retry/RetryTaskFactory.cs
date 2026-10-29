// <copyright file="RetryTaskFactory.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// A task factory for retriable tasks. Use this through the <see cref="RetryTask.Factory"/>.
/// </summary>
/// <remarks>
/// This is analogous to the <see cref="TaskFactory"/>, but provides tasks with built-in retry semantics.
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
    /// The strategy that the overloads without a strategy parameter use: up to 5 tries, without delay.
    /// </summary>
    /// <remarks>Strategies hold no state for a call, so one instance serves every call.</remarks>
    internal static readonly IRetryStrategy DefaultStrategy = new Count();

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

        return this.StartNew(action, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, cancellationToken, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, taskCreationOptions, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, state, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, state, taskCreationOptions, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, DefaultStrategy, AnyExceptionPolicy.Default);
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

        return this.StartNew(action, DefaultStrategy, policy);
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

        return this.StartNew(action, cancellationToken, DefaultStrategy, policy);
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

        return this.StartNew(action, taskCreationOptions, DefaultStrategy, policy);
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

        return this.StartNew(action, state, DefaultStrategy, policy);
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

        return this.StartNew(action, state, taskCreationOptions, DefaultStrategy, policy);
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

        return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, DefaultStrategy, policy);
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

        return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, DefaultStrategy, policy);
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
    /// A task that completes with the result of the first successful try; faults with the exceptions of every failed
    /// try when the operation will not be retried; or is
    /// cancelled, with <paramref name="cancellationToken"/>, when cancellation stops the operation.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The tries must be created with <see cref="Detached(TaskCreationOptions)"/> options. If the caller asked for
    /// <see cref="TaskCreationOptions.AttachedToParent"/>, it is the returned task that attaches to the parent, so
    /// that the parent sees the outcome of the operation rather than the failures of tries that were retried.
    /// </para>
    /// <para>
    /// No thread is blocked while the operation is retried. Retry decisions run on the thread pool, each delay is a
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="RetryDefaults.TimeProvider"/>
    /// with a continuation, and each retry is queued to <paramref name="scheduler"/>.
    /// </para>
    /// <para>
    /// This is used by both the <see cref="RetryTaskFactory"/> and the <see cref="RetryTaskFactory{T}"/>.
    /// </para>
    /// </remarks>
    internal static Task<TResult> Start<TResult>(Task firstTry, Func<Task> createTask, Func<Task, TResult> getResult, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        var operation = new RetryOperation<TResult>(createTask, getResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
        operation.ExamineWhenComplete(firstTry);
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
    /// An operation being retried by <see cref="Start{TResult}"/>, and the task that reports its outcome.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <remarks>
    /// <para>
    /// This holds everything the continuations need, so that each continuation can be a static delegate. It also
    /// holds the state of the call (the attempt number, the last delay and the failures), so that the strategy holds
    /// none.
    /// </para>
    /// <para>
    /// Retry decisions run on the thread pool; a delay is a
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="TimeProvider"/> with a
    /// continuation; no thread is blocked. Only one try runs at a time, and each step starts after the previous one
    /// has ended, so the state of the call is never used concurrently.
    /// </para>
    /// </remarks>
    private sealed class RetryOperation<TResult> : TaskCompletionSource<TResult>
    {
        // The operation is passed as each continuation's state, so the continuations need no closure.
        private static readonly Action<Task, object?> OnTryCompleted = static (t, state) => ((RetryOperation<TResult>)state!).Complete(t);
        private static readonly Action<Task, object?> OnDelayCompleted = static (t, state) => ((RetryOperation<TResult>)state!).AfterDelay(t);

        private readonly Func<Task> createTask;
        private readonly Func<Task, TResult> getResult;
        private readonly CancellationToken cancellationToken;
        private readonly TaskScheduler scheduler;
        private readonly IRetryStrategy strategy;
        private readonly IRetryPolicy policy;
        private readonly TimeProvider timeProvider = RetryDefaults.TimeProvider;

        // The state of this call. The strategy holds none, so one strategy can serve many calls at the same time.
        private int attemptNumber = 1;
        private TimeSpan previousDelay;

        // Created on the first failure, so that an operation that succeeds at its first try allocates no list.
        private List<Exception>? failures;

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

        /// <summary>
        /// Examines the outcome of a try when it completes.
        /// </summary>
        /// <param name="task">The task running the try.</param>
        /// <remarks>
        /// The examination runs on the thread pool, not the caller's scheduler, so that it does not wait behind (or
        /// hold up) other work on that scheduler, such as a UI thread.
        /// </remarks>
        public void ExamineWhenComplete(Task task)
        {
            task.ContinueWith(OnTryCompleted, this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void Complete(Task task)
        {
            try
            {
                if (task.IsCanceled)
                {
                    // A try acknowledged cancellation, or the token was cancelled before it could start.
                    this.TrySetCanceled(this.cancellationToken);
                }
                else if (task.Exception is { } exception)
                {
                    this.RetryOrFail(exception);
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

        private void RetryOrFail(AggregateException exception)
        {
            this.Record(exception);

            TimeSpan? retryDelay = this.strategy.GetRetryDelay(new RetryAttempt(this.attemptNumber, exception, null, this.previousDelay));

            if (retryDelay is null || !this.policy.CanRetryAll(exception))
            {
                // Report the failures of every try, not only the last one.
                this.TrySetException(new AggregateException(this.failures!));
                return;
            }

            // Check before starting a delay for a retry that will not run.
            if (this.cancellationToken.IsCancellationRequested)
            {
                this.TrySetCanceled(this.cancellationToken);
                return;
            }

            TimeSpan delay = RetryDelay.Clamp(retryDelay.Value);
            this.attemptNumber++;
            this.previousDelay = delay;

            if (delay == TimeSpan.Zero)
            {
                this.StartNextTry();
                return;
            }

            // The delay ends early if cancellation is requested. Its continuation only queues the next try, so it is
            // cheap enough to run on the thread that completes the delay.
            RetryDelay.WaitAsync(delay, this.timeProvider, this.cancellationToken).ContinueWith(
                OnDelayCompleted,
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Records the failure of a try.
        /// </summary>
        /// <param name="exception">The exception of the try's task.</param>
        /// <remarks>
        /// The inner exceptions are recorded, so that the task's own <see cref="AggregateException"/> does not wrap
        /// each failure. An <see cref="AggregateException"/> with no inner exceptions is recorded as itself, so that
        /// the failure is not lost.
        /// </remarks>
        private void Record(AggregateException exception)
        {
            List<Exception> failures = this.failures ??= [];
            if (exception.InnerExceptions.Count == 0)
            {
                failures.Add(exception);
            }
            else
            {
                failures.AddRange(exception.InnerExceptions);
            }
        }

        private void AfterDelay(Task delay)
        {
            try
            {
                if (delay.IsCanceled || this.cancellationToken.IsCancellationRequested)
                {
                    this.TrySetCanceled(this.cancellationToken);
                }
                else
                {
                    this.StartNextTry();
                }
            }
            catch (Exception ex)
            {
                // Nothing observes this continuation's task, so a failure must fault the operation instead.
                this.TrySetException(ex);
            }
        }

        private void StartNextTry()
        {
            Task next = this.createTask();
            if (next.IsCanceled)
            {
                // A task created with a cancelled token is cancelled before it starts.
                this.TrySetCanceled(this.cancellationToken);
                return;
            }

            // Attach the continuation before the try starts, so that the try cannot complete before it is attached.
            this.ExamineWhenComplete(next);

            try
            {
                next.Start(this.scheduler);
            }
            catch (InvalidOperationException) when (next.IsCanceled)
            {
                // The token was cancelled after the check above, but before the retry could start.
                this.TrySetCanceled(this.cancellationToken);
            }
            catch (TaskSchedulerException)
            {
                // The scheduler could not queue the retry, so the retry's task is faulted with this exception. Its
                // continuation examines that failure like any other, so there is nothing more to do here.
            }
        }
    }
}
