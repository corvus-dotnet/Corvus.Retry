// <copyright file="FallbackOptions{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The settings of the fallback of a <see cref="ReliabilityPipeline{T}"/>: the value that the pipeline gives when all the
/// other parts of the pipeline fail.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <para>
/// The fallback is the outermost part of the pipeline. It receives the exception or the result after the retries, the
/// total timeout and the circuit breaker. It applies to a <see cref="RetryTimeoutException"/> and to a
/// <see cref="BrokenCircuitException"/>. It never applies when the caller cancels the token.
/// </para>
/// <para>
/// The fallback applies only to the outcome of the attempts. It does not apply to an exception from a callback, a
/// strategy or a policy of the pipeline, for example <see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/>,
/// <see cref="ReliabilityPipelineOptions.OnRetry"/>, the strategy, the retry policy, the policy or the state change
/// callback of the circuit breaker, <see cref="HedgingOptions.OnHedging"/>, or
/// <see cref="ReliabilityPipelineOptions{T}.HedgingActionGenerator"/>. Such an exception stops the call.
/// </para>
/// <para>
/// The options are immutable. The constructor of the pipeline checks them.
/// </para>
/// </remarks>
public sealed record FallbackOptions<T>
{
    /// <summary>
    /// Gets the action that gives the fallback value.
    /// </summary>
    /// <remarks>
    /// The pipeline returns the value of the action. An exception from the action stops the call; the pipeline does
    /// not catch it.
    /// </remarks>
    public required Func<FallbackArgs<T>, ValueTask<T>> Action { get; init; }

    /// <summary>
    /// Gets the policy that decides whether an exception from the pipeline gets the fallback value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. When the policy refuses an exception, the pipeline
    /// throws that exception.
    /// </para>
    /// <para>
    /// The policy is asked about the exceptions that the operation actually threw. When the exception is an
    /// <see cref="AggregateException"/>, the policy is asked about each exception inside it, depth first, and the
    /// fallback applies only when the policy accepts all of them. <see cref="FallbackArgs{T}.Exception"/> is the
    /// <see cref="AggregateException"/> itself.
    /// </para>
    /// </remarks>
    public IRetryPolicy Policy { get; init; } = AnyExceptionPolicy.Default;

    /// <summary>
    /// Gets a predicate that returns <see langword="true"/> for a result that gets the fallback value, or
    /// <see langword="null"/> if no result gets the fallback value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate receives the result that the other parts of the pipeline return. Before the pipeline calls
    /// <see cref="OnFallback"/> and <see cref="Action"/>, it disposes a rejected result that implements
    /// <see cref="IDisposable"/>. An exception from that disposal stops the call: the caller gets that exception, and
    /// the pipeline does not call <see cref="OnFallback"/> or <see cref="Action"/>.
    /// </para>
    /// <para>
    /// An exception from the predicate stops the call: the caller gets that exception. The pipeline disposes the result
    /// that it does not return, and ignores an exception from that disposal.
    /// </para>
    /// </remarks>
    public Func<T, bool>? ShouldFallbackResult { get; init; }

    /// <summary>
    /// Gets a callback that the pipeline awaits before it calls <see cref="Action"/>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback receives the exception or the result that caused the fallback. An exception from the callback
    /// stops the call, and the pipeline does not call <see cref="Action"/>.
    /// </remarks>
    public Func<FallbackArgs<T>, ValueTask>? OnFallback { get; init; }
}
