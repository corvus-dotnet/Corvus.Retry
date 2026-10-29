// <copyright file="IRetryOperation{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// An asynchronous operation that <see cref="RetryEngine.RunAsync{TOp, T}"/> retries.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// Implementations are readonly structs that hold the caller's delegate. The engine is generic on the struct type, so
/// the JIT makes a special version of the loop for each implementation, and no closure or interface call is needed.
/// </remarks>
internal interface IRetryOperation<T>
{
    /// <summary>
    /// Runs one attempt of the operation.
    /// </summary>
    /// <param name="cancellationToken">The token for this attempt.</param>
    /// <returns>The result of the attempt.</returns>
    ValueTask<T> InvokeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Determines whether a result that the operation returned is a failure that the engine can retry.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns><see langword="true"/> if the result is a failure; otherwise, <see langword="false"/>.</returns>
    bool IsFailure(T result);
}
