// <copyright file="Retriable.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
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
/// </remarks>
public static class Retriable
{
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
    /// <param name="policy">The retry policy to use.</param>
    /// <param name="onRetry">
    /// An optional callback that is called before the delay of each retry. It is not called for the final failure. If
    /// it returns a task that is not complete, the thread waits for it. An exception from the callback stops the retries.
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
    /// <param name="policy">The retry policy to use.</param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
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
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context or not.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
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
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
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
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context or not.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which, when complete, provides the result of the function.</returns>
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
    /// <param name="func">The operatoin to retry.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">
    /// An optional callback that is called before the delay of each retry. It is not called for the final failure. If
    /// it returns a task that is not complete, the thread waits for it. An exception from the callback stops the retries.
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
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the operation is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
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
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which completes when the operation completes.</returns>
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
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the operation is complete.</returns>
    /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
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
    /// <param name="policy">The retry policy.</param>
    /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
    /// <param name="onRetry">
    /// An optional callback that is called, and awaited, before the delay of each retry. It is not called for the final
    /// failure. An exception from the callback stops the retries.
    /// </param>
    /// <returns>A task which completes when the operation completes.</returns>
    public static Task RetryAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        return RunAsync(new RetryOperations.TaskTokenUnitOperation(asyncFunc), cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
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
        return RetryEngine.RunAsync<TOp, T>(operation, cancellationToken, strategy, policy, RetryDefaults.TimeProvider, null, null, onRetry, continueOnCapturedContext).AsTask();
    }

    private static Task RunAsync<TOp>(TOp operation, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext, Func<RetryEventArgs, ValueTask>? onRetry)
        where TOp : struct, IRetryOperation<Unit>
    {
        ValueTask<Unit> run = RetryEngine.RunAsync<TOp, Unit>(operation, cancellationToken, strategy, policy, RetryDefaults.TimeProvider, null, null, onRetry, continueOnCapturedContext);

        // A call that completed synchronously needs no task of its own. Otherwise the engine's task is returned as it is.
        return run.IsCompletedSuccessfully ? Task.CompletedTask : run.AsTask();
    }
}
