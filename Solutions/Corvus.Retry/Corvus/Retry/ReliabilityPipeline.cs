// <copyright file="ReliabilityPipeline.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Runs operations, and retries them when they fail, with the settings of a <see cref="ReliabilityPipelineOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// A pipeline is immutable and safe for many threads. Each call has its own state, so one pipeline can serve many
/// calls at the same time.
/// </para>
/// <para>
/// To retry a result that is a failure, use <see cref="ReliabilityPipeline{T}"/>.
/// </para>
/// <para>
/// When <see cref="ReliabilityPipelineOptions.Hedging"/> is set, hedging replaces the retries: the pipeline starts more
/// attempts in parallel, and the first attempt that succeeds wins. When every attempt fails, the call throws the last
/// exception.
/// </para>
/// </remarks>
public sealed class ReliabilityPipeline
{
    private const string NestedTaskMessage = "ExecuteAsync received an operation that returns a task inside its ValueTask. Await the task in the operation.";

    private readonly IRetryStrategy strategy;
    private readonly IRetryPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly Func<RetryEventArgs, ValueTask>? onRetry;
    private readonly CircuitBreaker? circuitBreaker;
    private readonly bool continueOnCapturedContext;
    private readonly HedgingOptions? hedging;

    /// <summary>
    /// Initialises a new instance of the <see cref="ReliabilityPipeline"/> class.
    /// </summary>
    /// <param name="options">The settings of the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="ReliabilityPipelineOptions.Policy"/>,
    /// <see cref="ReliabilityPipelineOptions.TimeProvider"/> or the <see cref="HedgingOptions.Policy"/> of
    /// <see cref="ReliabilityPipelineOptions.Hedging"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ReliabilityPipelineOptions.AttemptTimeout"/> or <see cref="ReliabilityPipelineOptions.TotalTimeout"/> is not
    /// greater than zero, or is greater than the longest time that a timer supports; or the
    /// <see cref="HedgingOptions.MaxHedgedAttempts"/> or the <see cref="HedgingOptions.Delay"/> of
    /// <see cref="ReliabilityPipelineOptions.Hedging"/> is out of range.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ReliabilityPipelineOptions.Hedging"/> and <see cref="ReliabilityPipelineOptions.Strategy"/> are both set; or
    /// <paramref name="options"/> is a <see cref="ReliabilityPipelineOptions{T}"/> that sets
    /// <see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/>, <see cref="ReliabilityPipelineOptions{T}.Fallback"/> or
    /// <see cref="ReliabilityPipelineOptions{T}.HedgingActionGenerator"/>. Only <see cref="ReliabilityPipeline{T}"/> can use
    /// these settings.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not correct. For
    /// the settings that only <see cref="ReliabilityPipeline{T}"/> can use, it is <c>options</c>.
    /// </remarks>
    public ReliabilityPipeline(ReliabilityPipelineOptions options)
    {
        ReliabilityPipelineOptions.Validate(options);

        this.strategy = options.Strategy ?? ReliabilityPipelineOptions.DefaultStrategy;
        this.policy = options.Policy;
        this.timeProvider = options.TimeProvider;
        this.attemptTimeout = options.AttemptTimeout;
        this.totalTimeout = options.TotalTimeout;
        this.onRetry = options.OnRetry;
        this.circuitBreaker = options.CircuitBreaker;
        this.continueOnCapturedContext = options.ContinueOnCapturedContext;
        this.hedging = options.Hedging;
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

        if (this.hedging is not null)
        {
            return HedgingEngine.RunAsync(
                operation,
                null,
                null,
                this.hedging,
                cancellationToken,
                this.timeProvider,
                this.attemptTimeout,
                this.totalTimeout,
                this.circuitBreaker,
                this.continueOnCapturedContext);
        }

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

        ValueTask<Unit> call = this.hedging is not null
            ? this.ExecuteHedgedAsync(operation, this.hedging, cancellationToken)
            : RetryEngine.RunAsync<RetryOperations.ValueTaskTokenUnitOperation, Unit>(
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

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its <see cref="ValueTask{TResult}"/> to
    /// <see cref="ExecuteAsync{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the <see cref="ValueTask{TResult}"/> of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>ExecuteAsync(async ct => GetAsync(ct))</c> binds to
    /// <see cref="ExecuteAsync{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/> with the task of
    /// <c>GetAsync</c> as the result. The operation does not wait for that task, so a failure of that task is not
    /// retried.
    /// </para>
    /// <para>Write <c>ExecuteAsync(async ct => await GetAsync(ct))</c>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public ValueTask<TTask> ExecuteAsync<TTask>(Func<CancellationToken, ValueTask<TTask>> operation, CancellationToken cancellationToken = default, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    // This is a separate method, so that a call without hedging does not allocate the closure.
    private ValueTask<Unit> ExecuteHedgedAsync(Func<CancellationToken, ValueTask> operation, HedgingOptions hedging, CancellationToken cancellationToken)
    {
        return HedgingEngine.RunAsync(
            token => new RetryOperations.ValueTaskTokenUnitOperation(operation).InvokeAsync(token),
            null,
            null,
            hedging,
            cancellationToken,
            this.timeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.circuitBreaker,
            this.continueOnCapturedContext);
    }
}
