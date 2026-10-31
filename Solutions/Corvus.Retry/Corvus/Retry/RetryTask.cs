// <copyright file="RetryTask.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;

/// <summary>
/// Provides a Retriable Task factory analogous to <see cref="Task.Factory"/>.
/// </summary>
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
/// <para>
/// <see cref="Run(Func{CancellationToken, Task}, CancellationToken)"/> and its overloads use <see cref="Factory"/>, which
/// makes up to 5 tries, without delay, and retries any exception. For other settings, make a
/// <see cref="RetryTaskFactory"/> and keep it in a field.
/// </para>
/// </remarks>
public static class RetryTask
{
    private const string NestedTaskMessage = "Run received an operation that returns a task inside its task. Await the task in the operation.";

    /// <summary>
    /// Gets the retriable task factory.
    /// </summary>
    /// <remarks>The factory has the default settings of <see cref="RetryTaskFactory()"/>.</remarks>
    public static RetryTaskFactory Factory { get; } = new RetryTaskFactory();

    /// <summary>
    /// Runs an operation that returns no result with <see cref="Factory"/>, and retries it when it throws an exception.
    /// </summary>
    /// <param name="operation">The operation. It receives the caller's token.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// See <see cref="RetryTaskFactory.Run(Func{CancellationToken, Task}, CancellationToken)"/>. The first attempt runs
    /// on the caller's thread until its first <see langword="await"/>.
    /// </remarks>
    public static Task Run(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        return Factory.Run(operation, cancellationToken);
    }

    /// <summary>
    /// Runs an operation that returns a result with <see cref="Factory"/>, and retries it when it throws an exception.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation. It receives the caller's token.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// See <see cref="RetryTaskFactory.Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>. The first attempt
    /// runs on the caller's thread until its first <see langword="await"/>.
    /// </remarks>
    public static Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        return Factory.Run(operation, cancellationToken);
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>Run(async ct => GetAsync(ct))</c> binds to
    /// <see cref="Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/> with the task of <c>GetAsync</c> as
    /// the result. The operation does not wait for that task, so a failure of that task is not retried.
    /// </para>
    /// <para>Write <c>Run(async ct => await GetAsync(ct))</c>, or <c>Run(ct => GetAsync(ct))</c>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public static Task<TTask> Run<TTask>(Func<CancellationToken, Task<TTask>> operation, CancellationToken cancellationToken = default, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }
}
