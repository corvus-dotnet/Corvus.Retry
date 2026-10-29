// <copyright file="RetryPipeline.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Runs operations, and retries them when they fail, with the settings of a <see cref="RetryPipelineOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// A pipeline is immutable and safe for many threads. Each call has its own state, so one pipeline can serve many
/// calls at the same time.
/// </para>
/// <para>
/// To retry a result that is a failure, use <see cref="RetryPipeline{T}"/>.
/// </para>
/// </remarks>
public sealed class RetryPipeline
{
    private readonly IRetryStrategy strategy;
    private readonly IRetryPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly Func<RetryEventArgs, ValueTask>? onRetry;
    private readonly CircuitBreaker? circuitBreaker;
    private readonly bool continueOnCapturedContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryPipeline"/> class.
    /// </summary>
    /// <param name="options">The settings of the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="RetryPipelineOptions.Policy"/> or
    /// <see cref="RetryPipelineOptions.TimeProvider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="RetryPipelineOptions.AttemptTimeout"/> or <see cref="RetryPipelineOptions.TotalTimeout"/> is not
    /// greater than zero, or is greater than the longest time that a timer supports.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not correct.
    /// </remarks>
    public RetryPipeline(RetryPipelineOptions options)
    {
        RetryPipelineOptions.Validate(options);

        this.strategy = options.Strategy ?? RetryPipelineOptions.DefaultStrategy;
        this.policy = options.Policy;
        this.timeProvider = options.TimeProvider;
        this.attemptTimeout = options.AttemptTimeout;
        this.totalTimeout = options.TotalTimeout;
        this.onRetry = options.OnRetry;
        this.circuitBreaker = options.CircuitBreaker;
        this.continueOnCapturedContext = options.ContinueOnCapturedContext;
    }

    /// <summary>
    /// Runs an operation that returns a result, and retries it when it throws an exception.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses a retry, the exception of the last attempt is rethrown with its original
    /// stack trace.
    /// </para>
    /// <para>
    /// The operation returns a <see cref="ValueTask{TResult}"/>. For a method that returns a <see cref="Task{TResult}"/>,
    /// write <c>ct => new ValueTask&lt;T&gt;(GetAsync(ct))</c>.
    /// </para>
    /// </remarks>
    public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RetryEngine.RunAsync<RetryOperations.ValueTaskTokenOperation<T>, T>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.timeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.onRetry,
            this.circuitBreaker,
            this.continueOnCapturedContext);
    }

    /// <summary>
    /// Runs an operation that returns no result, and retries it when it throws an exception.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses a retry, the exception of the last attempt is rethrown with its original
    /// stack trace.
    /// </para>
    /// <para>
    /// The operation returns a <see cref="ValueTask"/>. For a method that returns a <see cref="Task"/>, write
    /// <c>ct => new ValueTask(DoAsync(ct))</c>.
    /// </para>
    /// </remarks>
    public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ValueTask<Unit> call = RetryEngine.RunAsync<RetryOperations.ValueTaskTokenUnitOperation, Unit>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.timeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.onRetry,
            this.circuitBreaker,
            this.continueOnCapturedContext);

        // A call that completed synchronously needs no task. Otherwise the task carries the outcome of the call.
        return call.IsCompletedSuccessfully ? default : new ValueTask(call.AsTask());
    }
}
