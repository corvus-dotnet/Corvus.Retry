// <copyright file="IRetrySyncOperation{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// A synchronous operation that <see cref="RetryEngine.Run{TOp, T}"/> retries.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// Implementations are readonly structs that hold the caller's delegate, for the same reason as
/// <see cref="IRetryOperation{T}"/>.
/// </remarks>
internal interface IRetrySyncOperation<T>
{
    /// <summary>
    /// Runs one attempt of the operation.
    /// </summary>
    /// <returns>The result of the attempt.</returns>
    T Invoke();
}
