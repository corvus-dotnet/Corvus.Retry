// <copyright file="HedgedTask.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs operations with hedging, with the default settings of <see cref="HedgedTaskFactory"/>.
/// </summary>
/// <remarks>
/// <see cref="Factory"/> starts 1 extra attempt when the first attempt has not succeeded after 2 seconds, or at once
/// when it fails. For other settings, make a <see cref="HedgedTaskFactory"/> and keep it in a field.
/// </remarks>
public static class HedgedTask
{
    private const string NestedTaskMessage = "Run received an operation that returns a task inside its task. Await the task in the operation.";

    /// <summary>
    /// Gets the hedged task factory with the default settings.
    /// </summary>
    public static HedgedTaskFactory Factory { get; } = new HedgedTaskFactory();

    /// <summary>
    /// Runs an operation that returns no result with hedging, with <see cref="Factory"/>.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels, or when another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="HedgedTaskFactory.Run(Func{CancellationToken, Task}, CancellationToken)"/>.</remarks>
    public static Task Run(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        return Factory.Run(operation, cancellationToken);
    }

    /// <summary>
    /// Runs an operation that returns a result with hedging, with <see cref="Factory"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels, or when another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="HedgedTaskFactory.Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>.</remarks>
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
    /// See <see cref="HedgedTaskFactory.Run{TTask}(Func{CancellationToken, Task{TTask}}, CancellationToken, object?[])"/>
    /// for the reason.
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
