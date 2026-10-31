// <copyright file="ReliabilityPipelineOptions{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The settings of a <see cref="ReliabilityPipeline{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <para>
/// These options add the settings that apply to the result of the operation.
/// </para>
/// <para>
/// Only <see cref="ReliabilityPipeline{T}"/> can use <see cref="ShouldRetryResult"/>, <see cref="Fallback"/> and
/// <see cref="HedgingActionGenerator"/>. If one of them is set, the constructor of <see cref="ReliabilityPipeline"/>
/// throws an <see cref="ArgumentException"/>. Without them, <see cref="ReliabilityPipeline"/> accepts these options.
/// </para>
/// </remarks>
public sealed record ReliabilityPipelineOptions<T> : ReliabilityPipelineOptions
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
    /// <para>
    /// An exception from that disposal stops the call: the caller gets that exception, the next attempt does not
    /// start, and the fallback does not apply, because the exception is not an outcome of the attempts.
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

    /// <summary>
    /// Gets a function that gives the operation for each attempt of hedging, or <see langword="null"/> to use the
    /// primary operation for every attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The function receives the number of the attempt, starting at 1, and can give a different operation for each
    /// attempt (for example, a different endpoint). When it returns <see langword="null"/>, the attempt uses the
    /// primary operation: the operation that the caller gives to <see cref="ReliabilityPipeline{T}.ExecuteAsync"/>. An
    /// exception from the function stops the call.
    /// </para>
    /// <para>
    /// The pipeline uses this function only when <see cref="ReliabilityPipelineOptions.Hedging"/> is set.
    /// </para>
    /// </remarks>
    public Func<int, Func<CancellationToken, ValueTask<T>>?>? HedgingActionGenerator { get; init; }

    /// <inheritdoc/>
    internal override bool HasResultSettings =>
        this.ShouldRetryResult is not null || this.Fallback is not null || this.HedgingActionGenerator is not null;
}
