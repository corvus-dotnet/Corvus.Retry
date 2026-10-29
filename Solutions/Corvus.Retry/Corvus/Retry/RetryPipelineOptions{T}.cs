// <copyright file="RetryPipelineOptions{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The settings of a <see cref="RetryPipeline{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// These options add the settings that apply to the result of the operation.
/// </remarks>
public sealed record RetryPipelineOptions<T> : RetryPipelineOptions
{
    /// <summary>
    /// Gets a predicate that returns <see langword="true"/> for a result that is a failure, or <see langword="null"/>
    /// if no result is a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline uses a result that the predicate rejects as a failure: the strategy, the callback and the delay
    /// apply, but the policy does not (the policy applies only to exceptions). An exception from the predicate stops
    /// the call.
    /// </para>
    /// <para>
    /// Before the next attempt, the pipeline disposes a rejected result that implements <see cref="IDisposable"/>. It
    /// does this after the callback. When no retries remain, the pipeline returns the last result, and does not
    /// dispose it.
    /// </para>
    /// </remarks>
    public Func<T, bool>? ShouldRetryResult { get; init; }

    /// <summary>
    /// Gets the fallback that gives a value when the pipeline fails, or <see langword="null"/> for no fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback is the outermost part of the pipeline. It receives the exception or the last result after the
    /// retries, the total timeout and the circuit breaker. It never applies when the caller cancels the token.
    /// </para>
    /// <para>
    /// <see cref="FallbackOptions{T}.Action"/> and <see cref="FallbackOptions{T}.Policy"/> must not be
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    public FallbackOptions<T>? Fallback { get; init; }
}
