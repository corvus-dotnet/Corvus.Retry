// <copyright file="RetriableDriver.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Runs an operation through the <see cref="Retriable"/> overload (taking a token, strategy and policy) for a given <see cref="RetryShape"/>,
/// so one test can assert a behaviour against every shape of operation.
/// </summary>
internal static class RetriableDriver
{
    /// <summary>
    /// Retries an operation that takes no token, using the overload for <paramref name="shape"/>.
    /// </summary>
    /// <param name="shape">Which overload to use.</param>
    /// <param name="operation">The operation. The action shapes discard its result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">
    /// The retry callback. When it is not <see langword="null"/>, the overload that has all the parameters is used.
    /// </param>
    /// <returns>The operation's result for the function shapes; <see langword="null"/> for the action shapes.</returns>
    public static Task<string?> RetryAsync(
        RetryShape shape,
        Func<string> operation,
        CancellationToken cancellationToken,
        IRetryStrategy strategy,
        IRetryPolicy policy,
        Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        return RetryAsync(shape, _ => operation(), cancellationToken, strategy, policy, onRetry);
    }

    /// <summary>
    /// Retries <paramref name="operation"/> using the overload for <paramref name="shape"/>.
    /// </summary>
    /// <param name="shape">Which overload to use.</param>
    /// <param name="operation">The operation. The action shapes discard its result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="onRetry">
    /// The retry callback. When it is not <see langword="null"/>, the overload that has all the parameters is used.
    /// </param>
    /// <returns>The operation's result for the function shapes; <see langword="null"/> for the action shapes.</returns>
    /// <remarks>
    /// The asynchronous shapes yield before running the operation, so a failure faults the returned task
    /// (as a real async method would) rather than throwing synchronously. The token shapes pass the token that they
    /// receive to the operation; the other shapes pass <see cref="CancellationToken.None"/>.
    /// </remarks>
    public static async Task<string?> RetryAsync(
        RetryShape shape,
        Func<CancellationToken, string> operation,
        CancellationToken cancellationToken,
        IRetryStrategy strategy,
        IRetryPolicy policy,
        Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        switch (shape)
        {
            case RetryShape.Action:
                Action action = () => operation(CancellationToken.None);
                Retriable.Retry(action, cancellationToken, strategy, policy, onRetry);
                return null;

            case RetryShape.Function:
                return Retriable.Retry(() => operation(CancellationToken.None), cancellationToken, strategy, policy, onRetry);

            case RetryShape.AsyncAction:
                Func<Task> asyncAction = async () =>
                {
                    await Task.Yield();
                    operation(CancellationToken.None);
                };
                await (onRetry is null
                    ? Retriable.RetryAsync(asyncAction, cancellationToken, strategy, policy)
                    : Retriable.RetryAsync(asyncAction, cancellationToken, strategy, policy, false, onRetry)).ConfigureAwait(false);
                return null;

            case RetryShape.AsyncFunction:
                Func<Task<string>> asyncFunction = async () =>
                {
                    await Task.Yield();
                    return operation(CancellationToken.None);
                };
                return await (onRetry is null
                    ? Retriable.RetryAsync(asyncFunction, cancellationToken, strategy, policy)
                    : Retriable.RetryAsync(asyncFunction, cancellationToken, strategy, policy, false, onRetry)).ConfigureAwait(false);

            case RetryShape.AsyncActionWithToken:
                Func<CancellationToken, Task> asyncActionWithToken = async ct =>
                {
                    await Task.Yield();
                    operation(ct);
                };
                await (onRetry is null
                    ? Retriable.RetryAsync(asyncActionWithToken, cancellationToken, strategy, policy)
                    : Retriable.RetryAsync(asyncActionWithToken, cancellationToken, strategy, policy, false, onRetry)).ConfigureAwait(false);
                return null;

            case RetryShape.AsyncFunctionWithToken:
                Func<CancellationToken, Task<string>> asyncFunctionWithToken = async ct =>
                {
                    await Task.Yield();
                    return operation(ct);
                };
                return await (onRetry is null
                    ? Retriable.RetryAsync(asyncFunctionWithToken, cancellationToken, strategy, policy)
                    : Retriable.RetryAsync(asyncFunctionWithToken, cancellationToken, strategy, policy, false, onRetry)).ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }
}
