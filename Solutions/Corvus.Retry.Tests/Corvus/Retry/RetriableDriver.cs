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
/// so one test can assert a behaviour against all four retry loops.
/// </summary>
internal static class RetriableDriver
{
    /// <summary>
    /// Retries <paramref name="operation"/> using the overload for <paramref name="shape"/>.
    /// </summary>
    /// <param name="shape">Which overload to use.</param>
    /// <param name="operation">The operation. The action shapes discard its result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="strategy">The retry strategy.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>The operation's result for the function shapes; <see langword="null"/> for the action shapes.</returns>
    /// <remarks>
    /// The asynchronous shapes yield before running the operation, so a failure faults the returned task
    /// (as a real async method would) rather than throwing synchronously.
    /// </remarks>
    public static async Task<string?> RetryAsync(
        RetryShape shape,
        Func<string> operation,
        CancellationToken cancellationToken,
        IRetryStrategy strategy,
        IRetryPolicy policy)
    {
        switch (shape)
        {
            case RetryShape.Action:
                Action action = () => operation();
                Retriable.Retry(action, cancellationToken, strategy, policy);
                return null;

            case RetryShape.Function:
                return Retriable.Retry(operation, cancellationToken, strategy, policy);

            case RetryShape.AsyncAction:
                Func<Task> asyncAction = async () =>
                {
                    await Task.Yield();
                    operation();
                };
                await Retriable.RetryAsync(asyncAction, cancellationToken, strategy, policy).ConfigureAwait(false);
                return null;

            case RetryShape.AsyncFunction:
                Func<Task<string>> asyncFunction = async () =>
                {
                    await Task.Yield();
                    return operation();
                };
                return await Retriable.RetryAsync(asyncFunction, cancellationToken, strategy, policy).ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }
}
