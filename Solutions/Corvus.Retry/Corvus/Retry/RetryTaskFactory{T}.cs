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
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, RetryTaskFactory.DefaultStrategy, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
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
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    public Task<T> StartNew(Func<T> function, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function), () => new Task<T>(function), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken), () => new Task<T>(function, cancellationToken), GetResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state), () => new Task<T>(function, state), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy);
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
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, state, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy);
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
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

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
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
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
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy);
    }

    private static T GetResult(Task task) => ((Task<T>)task).Result;
}