// <copyright file="Retriable.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Methods to provide retry semantics for synchronous and asynchronous operations.
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
/// <para>
/// The overloads that take a cancellation token check it before each attempt, so an attempt never starts after the
/// token is cancelled, and the token also stops a delay between attempts. Either way, the call throws an
/// <see cref="OperationCanceledException"/>. A cancellation that the token caused is never retried.
/// </para>
/// <para>
/// The policy is asked about the exceptions that the operation actually threw, as <see cref="RetryTask"/> does. When
/// the operation throws an <see cref="AggregateException"/>, the policy is asked about each exception inside it, depth
/// first, until it refuses one, and the operation is retried only if the policy accepts all of them. An
/// <see cref="AggregateException"/> with no inner exceptions is passed to the policy itself. The strategy gets the
/// exception as the operation threw it, in <see cref="RetryAttempt.Exception"/>, and a call that is not retried throws
/// that exception.
/// </para>
/// </remarks>
public static class Retriable
{
    private const string AsyncRetryMessage = "Retry does not retry an asynchronous operation. Use RetryAsync, or ReliabilityPipeline.ExecuteAsync for a ValueTask.";
    private const string NestedTaskMessage = "RetryAsync received an operation that returns a task inside its task. Await the task in the operation.";

    /// <summary>
    /// Retries an operation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="func">The function to retry.</param>
    /// <returns>The result of the function.</returns>
    public static T Retry<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        return Run<RetryOperations.FuncOperation<T>, T>(new(func), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, null);
    }

    /// <summary>
    /// Retries an asynchronous operation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">The async function to retry.</param>
    /// <returns>A task which, when completes, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync<RetryOperations.TaskOperation<T>, T>(new(asyncFunc), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">The async function to retry.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context as per "await" semantics.</param>
    /// <returns>A task which, when completes, provides the result of the function.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, bool continueOnCapturedContext)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync<RetryOperations.TaskOperation<T>, T>(new(asyncFunc), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, continueOnCapturedContext, null);
    }

    /// <summary>
    /// Retries an operation.
    /// </summary>
    /// <param name="func">The function to retry.</param>
    public static void Retry(Action func)
    {
        ArgumentNullException.ThrowIfNull(func);

        Run<RetryOperations.ActionOperation, Unit>(new(func), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, null);
    }

    /// <summary>
    /// Retries an operation with cancellation.
    /// </summary>
    /// <param name="func">The function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static void Retry(Action func, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(func);

        Run<RetryOperations.ActionOperation, Unit>(new(func), cancellationToken, RetryStrategies.Default, AnyExceptionPolicy.Default, null);
    }

    /// <summary>
    /// Retries an asynchronous operation.
    /// </summary>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <returns>A Task which completes when the function is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<Task> asyncFunc)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync(new RetryOperations.TaskUnitOperation(asyncFunc), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A Task which completes when the function is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync(new RetryOperations.TaskUnitOperation(asyncFunc), cancellationToken, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation.
    /// </summary>
    /// <param name="asyncFunc">The async function to retry.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context as per "await" semantics.</param>
    /// <returns>A task which, when completes, provides the result of the function.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<Task> asyncFunc, bool continueOnCapturedContext)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync(new RetryOperations.TaskUnitOperation(asyncFunc), CancellationToken.None, RetryStrategies.Default, AnyExceptionPolicy.Default, continueOnCapturedContext, null);
    }

    /// <summary>
    /// Retries an operation with cancellation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="func">The function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy to use.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="onRetry">
    /// An optional callback that is called before the delay of each retry. It is not called for the final failure. If
    /// it returns a task that is not complete, the thread waits for it. An exception from the callback stops the retries.
    /// The callback runs with no <see cref="SynchronizationContext"/>. If it awaits, the continuation runs on the thread pool.
    /// </param>
    /// <returns>The result of the function.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static T Retry<T>(Func<T> func, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(func);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return Run<RetryOperations.FuncOperation<T>, T>(new(func), cancellationToken, strategy, policy, onRetry);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy to use.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync<RetryOperations.TaskOperation<T>, T>(new(asyncFunc), cancellationToken, strategy, policy, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync<RetryOperations.TaskOperation<T>, T>(new(asyncFunc), cancellationToken, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context or not.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync<RetryOperations.TaskOperation<T>, T>(new(asyncFunc), cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> asyncFunc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync<RetryOperations.TaskTokenOperation<T>, T>(new(asyncFunc), cancellationToken, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync<RetryOperations.TaskTokenOperation<T>, T>(new(asyncFunc), cancellationToken, strategy, policy, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <typeparam name="T">The return type of the function to retry.</typeparam>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context or not.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync<RetryOperations.TaskTokenOperation<T>, T>(new(asyncFunc), cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
    }

    /// <summary>
    ///  Retries an operation with cancellation.
    /// </summary>
    /// <param name="func">The operation to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="onRetry">
    /// An optional callback that is called before the delay of each retry. It is not called for the final failure. If
    /// it returns a task that is not complete, the thread waits for it. An exception from the callback stops the retries.
    /// The callback runs with no <see cref="SynchronizationContext"/>. If it awaits, the continuation runs on the thread pool.
    /// </param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static void Retry(Action func, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(func);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        Run<RetryOperations.ActionOperation, Unit>(new(func), cancellationToken, strategy, policy, onRetry);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which to signal cancellation.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <returns>A task which completes when the operation is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync(new RetryOperations.TaskUnitOperation(asyncFunc), cancellationToken, strategy, policy, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation with cancellation.
    /// </summary>
    /// <param name="asyncFunc">The asynchronous function to retry.</param>
    /// <param name="cancellationToken">The token with which to signal cancellation.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which completes when the operation completes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync(new RetryOperations.TaskUnitOperation(asyncFunc), cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which to signal cancellation.</param>
    /// <returns>A task which completes when the operation is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);

        return RunAsync(new RetryOperations.TaskTokenUnitOperation(asyncFunc), cancellationToken, RetryStrategies.Default, AnyExceptionPolicy.Default, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which to signal cancellation.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <returns>A task which completes when the operation is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync(new RetryOperations.TaskTokenUnitOperation(asyncFunc), cancellationToken, strategy, policy, false, null);
    }

    /// <summary>
    /// Retries an asynchronous operation that takes a cancellation token.
    /// </summary>
    /// <param name="asyncFunc">
    /// The asynchronous function to retry. It receives a token that is cancelled when <paramref name="cancellationToken"/> is cancelled.
    /// </param>
    /// <param name="cancellationToken">The token with which to signal cancellation.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">
    /// The retry policy. It is asked about the exceptions that the operation threw, not about an
    /// <see cref="AggregateException"/> that contains them. See the remarks of <see cref="Retriable"/>.
    /// </param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which completes when the operation completes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation returned a null task. The strategy and the policy decide whether it is retried, as for any other exception.
    /// </exception>
    public static Task RetryAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync(new RetryOperations.TaskTokenUnitOperation(asyncFunc), cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
    }

    /// <summary>
    /// Stops the compiler from binding an asynchronous operation to <see cref="Retry{T}(Func{T})"/> or
    /// <see cref="Retry(Action)"/>, and their overloads.
    /// </summary>
    /// <typeparam name="TTask">The type of task that the operation returns.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>Retry(async () => ...)</c> binds to <see cref="Retry{T}(Func{T})"/> with a task as
    /// the result. That overload retries only an exception that the lambda throws before its first
    /// <see langword="await"/>, and returns the task of the first attempt.
    /// </para>
    /// <para>
    /// This overload has a higher priority than the other <c>Retry</c> overloads. Thus, a call with an operation that
    /// returns a <see cref="Task"/> binds to it, and the compiler reports an error. A lambda that only throws has no
    /// return type, so it does not bind to this overload.
    /// </para>
    /// <para>Use <see cref="RetryAsync(Func{Task})"/>, <see cref="RetryAsync{T}(Func{Task{T}})"/>, or one of their overloads.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static TTask Retry<TTask>(Func<TTask> asyncFunc, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an asynchronous operation to
    /// <see cref="Retry{T}(Func{T}, CancellationToken, IRetryStrategy, IRetryPolicy, Func{RetryEventArgs, ValueTask}?)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of task that the operation returns.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">The callback before each retry.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// A lambda for <paramref name="onRetry"/> does not convert to <see cref="object"/>, so
    /// <see cref="Retry{TTask}(Func{TTask}, object?[])"/> cannot catch this call. See that overload for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static TTask Retry<TTask>(Func<TTask> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a <see cref="ValueTask"/> to
    /// <see cref="Retry{T}(Func{T})"/> or its overloads.
    /// </summary>
    /// <typeparam name="TValueTask">The type that the operation returns. Only <see cref="ValueTask"/> meets the constraints.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>Retry(async ValueTask () => ...)</c> binds to <see cref="Retry{T}(Func{T})"/> with
    /// the <see cref="ValueTask"/> as the result. That overload does not wait for the operation, and does not retry a
    /// failure after the first <see langword="await"/>.
    /// </para>
    /// <para>
    /// This method is generic, so that a lambda that only throws does not bind to it. Its last parameter is a span,
    /// so that its signature is different from <see cref="Retry{TTask}(Func{TTask}, object?[])"/>.
    /// </para>
    /// <para>Use <see cref="RetryAsync(Func{Task})"/>, or <see cref="ReliabilityPipeline.ExecuteAsync(Func{CancellationToken, ValueTask}, CancellationToken)"/>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static TValueTask Retry<TValueTask>(Func<TValueTask> asyncFunc, params ReadOnlySpan<object?> arguments)
        where TValueTask : struct, IEquatable<ValueTask>
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a <see cref="ValueTask{TResult}"/> to
    /// <see cref="Retry{T}(Func{T})"/> or its overloads.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>Retry(() => stream.ReadAsync(buffer))</c> binds to <see cref="Retry{T}(Func{T})"/>
    /// with the <see cref="ValueTask{TResult}"/> as the result. That overload does not wait for the operation, and does
    /// not retry a failure after the first <see langword="await"/>.
    /// </para>
    /// <para>Use <see cref="RetryAsync{T}(Func{Task{T}})"/>, or <see cref="ReliabilityPipeline.ExecuteAsync{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static ValueTask<TResult> Retry<TResult>(Func<ValueTask<TResult>> asyncFunc, params object?[] arguments)
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a <see cref="ValueTask"/> to
    /// <see cref="Retry{T}(Func{T}, CancellationToken, IRetryStrategy, IRetryPolicy, Func{RetryEventArgs, ValueTask}?)"/>.
    /// </summary>
    /// <typeparam name="TValueTask">The type that the operation returns. Only <see cref="ValueTask"/> meets the constraints.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">The callback before each retry.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the other overloads.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// A lambda for <paramref name="onRetry"/> does not convert to <see cref="object"/>, so
    /// <see cref="Retry{TValueTask}(Func{TValueTask}, ReadOnlySpan{object?})"/> cannot catch this call. See that
    /// overload for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static TValueTask Retry<TValueTask>(Func<TValueTask> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry, params ReadOnlySpan<object?> arguments)
        where TValueTask : struct, IEquatable<ValueTask>
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a <see cref="ValueTask{TResult}"/> to
    /// <see cref="Retry{T}(Func{T}, CancellationToken, IRetryStrategy, IRetryPolicy, Func{RetryEventArgs, ValueTask}?)"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">The callback before each retry.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the other overloads.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// A lambda for <paramref name="onRetry"/> does not convert to <see cref="object"/>, so
    /// <see cref="Retry{TResult}(Func{ValueTask{TResult}}, object?[])"/> cannot catch this call. See that overload for
    /// the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncRetryMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static ValueTask<TResult> Retry<TResult>(Func<ValueTask<TResult>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry, params object?[] arguments)
    {
        throw new NotSupportedException(AsyncRetryMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="RetryAsync{T}(Func{Task{T}})"/> or its overloads.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>RetryAsync(async () => GetAsync())</c> binds to
    /// <see cref="RetryAsync{T}(Func{Task{T}})"/> with the task of <c>GetAsync</c> as the result. The operation does
    /// not wait for that task, so a failure of that task is not retried.
    /// </para>
    /// <para>Write <c>RetryAsync(async () => await GetAsync())</c>, or <c>RetryAsync(() => GetAsync())</c>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static Task<TTask> RetryAsync<TTask>(Func<Task<TTask>> asyncFunc, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="RetryAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>See <see cref="RetryAsync{TTask}(Func{Task{TTask}}, object?[])"/> for the reason.</remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static Task<TTask> RetryAsync<TTask>(Func<CancellationToken, Task<TTask>> asyncFunc, CancellationToken cancellationToken, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="RetryAsync{T}(Func{Task{T}}, CancellationToken, IRetryStrategy, IRetryPolicy, bool, Func{RetryEventArgs, ValueTask}?)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">The callback before each retry.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// A lambda for <paramref name="onRetry"/> does not convert to <see cref="object"/>, so
    /// <see cref="RetryAsync{TTask}(Func{Task{TTask}}, object?[])"/> cannot catch this call. See that overload for the
    /// reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static Task<TTask> RetryAsync<TTask>(Func<Task<TTask>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="RetryAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken, IRetryStrategy, IRetryPolicy, bool, Func{RetryEventArgs, ValueTask}?)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="asyncFunc">The asynchronous operation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">The callback before each retry.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// A lambda for <paramref name="onRetry"/> does not convert to <see cref="object"/>, so
    /// <see cref="RetryAsync{TTask}(Func{CancellationToken, Task{TTask}}, CancellationToken, object?[])"/> cannot catch
    /// this call. See <see cref="RetryAsync{TTask}(Func{Task{TTask}}, object?[])"/> for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static Task<TTask> RetryAsync<TTask>(Func<CancellationToken, Task<TTask>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    // The public overloads validate the arguments, so that a null argument throws before a task is created.
    // Retriable has no timeouts, so the engine creates no cancellation source for a call.
    private static T Run<TOp, T>(TOp operation, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, Func<RetryEventArgs, ValueTask>? onRetry)
        where TOp : struct, IRetrySyncOperation<T>
    {
        return RetryEngine.Run<TOp, T>(operation, cancellationToken, strategy, policy, RetryDefaults.TimeProvider, onRetry);
    }

    private static Task<T> RunAsync<TOp, T>(TOp operation, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry)
        where TOp : struct, IRetryOperation<T>
    {
        return RetryEngine.RunAsync<TOp, T>(operation, cancellationToken, strategy, policy, RetryDefaults.TimeProvider, null, null, onRetry, null, continueOnCapturedContext).AsTask();
    }

    private static Task RunAsync<TOp>(TOp operation, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry)
        where TOp : struct, IRetryOperation<Unit>
    {
        ValueTask<Unit> run = RetryEngine.RunAsync<TOp, Unit>(operation, cancellationToken, strategy, policy, RetryDefaults.TimeProvider, null, null, onRetry, null, continueOnCapturedContext);

        // A call that completed synchronously needs no task of its own. Otherwise the engine's task is returned as it is.
        return run.IsCompletedSuccessfully ? Task.CompletedTask : run.AsTask();
    }
}
