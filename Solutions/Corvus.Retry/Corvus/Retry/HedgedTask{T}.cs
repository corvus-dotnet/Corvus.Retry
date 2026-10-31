// <copyright file="HedgedTask{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs operations that return a <typeparamref name="T"/> with hedging, with the default settings of
/// <see cref="HedgedTaskFactory{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <see cref="Factory"/> starts 1 extra attempt when the first attempt has not succeeded after 2 seconds, or at once
/// when it fails. For other settings, make a <see cref="HedgedTaskFactory{T}"/> and keep it in a field.
/// </remarks>
public static class HedgedTask<T>
{
    /// <summary>
    /// Gets the hedged task factory with the default settings.
    /// </summary>
    public static HedgedTaskFactory<T> Factory { get; } = new HedgedTaskFactory<T>();

    /// <summary>
    /// Runs an operation with hedging, with <see cref="Factory"/>.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels, or when another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="HedgedTaskFactory{T}.Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>.</remarks>
    public static Task<T> Run(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        return Factory.Run(operation, cancellationToken);
    }
}
