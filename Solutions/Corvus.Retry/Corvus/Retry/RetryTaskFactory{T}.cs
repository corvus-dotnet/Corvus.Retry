// <copyright file="RetryTaskFactory{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// A task factory for retriable tasks. Use this through the <see cref="RetryTask{T}.Factory"/>.
/// </summary>
/// <typeparam name="T">The type of the result of the Task.</typeparam>
/// <remarks>
/// This is analogous to the <see cref="TaskFactory{T}"/>, but provides tasks with built-in retry semantics.
/// <para>
/// There are three different ways of retrying tasks in this library.
/// <list type="number">
/// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
/// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
/// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
/// </list>
/// </para>
/// <para>
/// After a try fails, the factory first gives the strategy a <see cref="RetryAttempt"/>. Its
/// <see cref="RetryAttempt.AttemptNumber"/> is the number of tries that have failed, starting at 1. Its
/// <see cref="RetryAttempt.PreviousDelay"/> is the delay before the try that failed, or <see cref="TimeSpan.Zero"/>
/// for the first try. Its <see cref="RetryAttempt.Exception"/> is the <see cref="AggregateException"/> of the task of
/// the failed try. If the strategy returns <see langword="null"/>, the function is not retried. Otherwise the policy
/// is asked about the exceptions that the try actually threw: the exceptions in that <see cref="AggregateException"/>,
/// and in any <see cref="AggregateException"/> that the function threw. The function is retried only if the policy
/// accepts all of them.
/// </para>
/// <para>
/// A try that cancels itself with an <see cref="OperationCanceledException"/> for a token other than the caller's has
/// failed: the strategy is given the <see cref="AggregateException"/> that contains it, and the policy is asked about
/// it. A try that acknowledges cancellation of the caller's token cancels the returned task, and the strategy and the
/// policy are not asked. If the strategy or the policy throws, the returned task faults with that exception, or is
/// cancelled if it is an <see cref="OperationCanceledException"/> and cancellation of the caller's token was requested.
/// The function is not retried.
/// </para>
/// <para>
/// When the function is not retried, the task faults with an <see cref="AggregateException"/> of the failures. Each
/// failure is the exception that a try threw. An <see cref="AggregateException"/> that the function threw is one
/// failure: it is not flattened. The task keeps at most 100 failures: the first 99 and the last.
/// </para>
/// <para>
/// Every try is queued to the same scheduler: the <c>scheduler</c> argument, or the <see cref="TaskScheduler.Current"/>
/// of the caller of <c>StartNew</c>. If the scheduler refuses to queue the first try, <c>StartNew</c> throws the
/// <see cref="TaskSchedulerException"/>, and the function does not run. If it refuses a retry, that try fails with the
/// <see cref="TaskSchedulerException"/>, and the strategy and the policy decide whether to retry, as for any other
/// failure. The strategy and the policy run on the thread pool, not on that scheduler.
/// </para>
/// <para>
/// <typeparamref name="T"/> cannot be <see cref="Task"/>, <see cref="Task{TResult}"/>, <see cref="ValueTask"/> or
/// <see cref="ValueTask{TResult}"/>. The task of a try completes when the function returns its task, before the
/// asynchronous operation ends, so a failure of the operation is not retried. For such a type, each
/// <c>StartNew</c> overload throws a <see cref="NotSupportedException"/> and does not run the function. Use
/// <see cref="Retriable.RetryAsync{T}(Func{Task{T}})"/>, or
/// <see cref="ReliabilityPipeline.ExecuteAsync{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/> for a
/// <see cref="ValueTask{TResult}"/>.
/// </para>
/// <para>
/// When <typeparamref name="T"/> is not a task type, but the function returns a task as a value of a wider type (for
/// example a <see cref="Task"/> as an <see cref="object"/>, <see cref="IAsyncResult"/> or <see cref="IDisposable"/>,
/// or a boxed <see cref="ValueTask"/>), <c>StartNew</c> cannot refuse the call before the function runs. Instead, the
/// result of each successful try is checked: if it is a task, the returned task faults with a
/// <see cref="NotSupportedException"/>, and the function is not retried.
/// </para>
/// </remarks>
public sealed class RetryTaskFactory<T>
{
    private const string TaskResultMessage = "RetryTask<T> cannot retry a function that returns a task: the task completes when the function returns, before the operation ends. Use Retriable.RetryAsync, or ReliabilityPipeline.ExecuteAsync for a ValueTask.";

    // A try of a function that returns a task completes before the asynchronous operation ends, so it is not retried.
    private static readonly bool ResultIsTask =
        typeof(Task).IsAssignableFrom(typeof(T)) ||
        typeof(T) == typeof(ValueTask) ||
        (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ValueTask<>));

    // Only object, or an interface that a task type implements (such as IAsyncResult), can hold a task when T is not
    // a task type. For any other T, the result is not checked, so that the check costs nothing.
    private static readonly bool ResultMayBeTask = !ResultIsTask && (typeof(T) == typeof(object) || typeof(T).IsInterface);

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, cancellationToken, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, taskCreationOptions, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, taskCreationOptions, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
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
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, cancellationToken, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, taskCreationOptions, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, taskCreationOptions, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, strategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, RetryTaskFactory.DefaultStrategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, cancellationToken, RetryTaskFactory.DefaultStrategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, taskCreationOptions, RetryTaskFactory.DefaultStrategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, RetryTaskFactory.DefaultStrategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, taskCreationOptions, RetryTaskFactory.DefaultStrategy, policy);
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
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, RetryTaskFactory.DefaultStrategy, policy);
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
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, RetryTaskFactory.DefaultStrategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function), () => new Task<T>(function), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken), () => new Task<T>(function, cancellationToken), GetResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state), () => new Task<T>(function, state), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, state, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, cancellationToken), () => new Task<T>(function, state, cancellationToken), GetResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
    }

    private static T GetResult(Task task)
    {
        T result = ((Task<T>)task).Result;

        // An exception here faults the operation's task, and is not retried.
        if (ResultMayBeTask && IsTask(result))
        {
            throw new NotSupportedException(TaskResultMessage);
        }

        return result;
    }

    private static bool IsTask(T result)
    {
        return result is Task or ValueTask
            || (result is not null && result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>));
    }

    private static void ThrowIfResultIsTask()
    {
        if (ResultIsTask)
        {
            throw new NotSupportedException(TaskResultMessage);
        }
    }
}