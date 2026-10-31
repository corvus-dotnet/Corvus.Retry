// <copyright file="RetryTask{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;

/// <summary>
/// Provides a Retriable Task factory analogous to <see cref="Task{T}.Factory"/>.
/// </summary>
/// <typeparam name="T">The return type of the task.</typeparam>
/// <remarks>
/// <para>
/// There are four ways to retry an operation in this library.
/// <list type="number">
/// <item><see cref="Retriable"/> puts retries around one call to a synchronous or an asynchronous operation.</item>
/// <item><see cref="RetryTask"/>, <see cref="RetryTask{T}"/> and a configured <see cref="RetryTaskFactory"/> or <see cref="RetryTaskFactory{T}"/> run an asynchronous operation with <c>Run</c>, or start a synchronous function as a task with <c>StartNew</c>. A factory adds timeouts, result retries, a circuit breaker and a fallback.</item>
/// <item><see cref="HedgedTask"/>, <see cref="HedgedTask{T}"/>, <see cref="HedgedTaskFactory"/> and <see cref="HedgedTaskFactory{T}"/> start more attempts in parallel when an attempt is slow or fails.</item>
/// <item><see cref="ReliableTaskRunner"/> runs a long-running asynchronous operation, and starts it again when it fails. An <see cref="IRetryPolicy"/> controls the restarts.</item>
/// </list>
/// </para>
/// </remarks>
public static class RetryTask<T>
{
    /// <summary>
    /// Gets the Factory used to create retry tasks.
    /// </summary>
    /// <remarks>The factory has the default settings of <see cref="RetryTaskFactory{T}()"/>.</remarks>
    public static RetryTaskFactory<T> Factory { get; } = new RetryTaskFactory<T>();

    /// <summary>
    /// Runs an operation with <see cref="Factory"/>, and retries it when it throws an exception.
    /// </summary>
    /// <param name="operation">The operation. It receives the caller's token.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is a task type.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// See <see cref="RetryTaskFactory{T}.Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>. The first attempt
    /// runs on the caller's thread until its first <see langword="await"/>.
    /// </remarks>
    public static Task<T> Run(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        return Factory.Run(operation, cancellationToken);
    }
}
