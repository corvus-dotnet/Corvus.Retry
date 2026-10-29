// <copyright file="ReliabilityPipeline{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Runs operations that return a <typeparamref name="T"/>, and retries them when they throw an exception or return a
/// result that is a failure, with the settings of a <see cref="ReliabilityPipelineOptions{T}"/>. A fallback can give a value
/// when the retries fail.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <para>
/// A pipeline is immutable and safe for many threads. Each call has its own state, so one pipeline can serve many
/// calls at the same time.
/// </para>
/// <para>
/// When <see cref="ReliabilityPipelineOptions.Hedging"/> is set, hedging replaces the retries: the pipeline starts more
/// attempts in parallel, and the first outcome that is not a failure wins. When every attempt fails, the call throws
/// the last exception or returns the last result. The fallback applies to that outcome.
/// </para>
/// </remarks>
public sealed class ReliabilityPipeline<T>
{
    private readonly IRetryStrategy strategy;
    private readonly IRetryPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly Func<RetryEventArgs, ValueTask>? onRetry;
    private readonly CircuitBreaker? circuitBreaker;
    private readonly bool continueOnCapturedContext;
    private readonly Func<T, bool>? shouldRetryResult;
    private readonly FallbackOptions<T>? fallback;
    private readonly HedgingOptions? hedging;
    private readonly Func<int, Func<CancellationToken, ValueTask<T>>?>? hedgingActionGenerator;

    /// <summary>
    /// Initialises a new instance of the <see cref="ReliabilityPipeline{T}"/> class.
    /// </summary>
    /// <param name="options">The settings of the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="ReliabilityPipelineOptions.Policy"/>,
    /// <see cref="ReliabilityPipelineOptions.TimeProvider"/>, the <see cref="FallbackOptions{T}.Action"/> or the
    /// <see cref="FallbackOptions{T}.Policy"/> of <see cref="ReliabilityPipelineOptions{T}.Fallback"/>, or the
    /// <see cref="HedgingOptions.Policy"/> of <see cref="ReliabilityPipelineOptions.Hedging"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ReliabilityPipelineOptions.AttemptTimeout"/> or <see cref="ReliabilityPipelineOptions.TotalTimeout"/> is not
    /// greater than zero, or is greater than the longest time that a timer supports; or the
    /// <see cref="HedgingOptions.MaxHedgedAttempts"/> or the <see cref="HedgingOptions.Delay"/> of
    /// <see cref="ReliabilityPipelineOptions.Hedging"/> is out of range.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ReliabilityPipelineOptions.Hedging"/> and <see cref="ReliabilityPipelineOptions.Strategy"/> are both set.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not correct.
    /// </remarks>
    public ReliabilityPipeline(ReliabilityPipelineOptions<T> options)
    {
        ReliabilityPipelineOptions.Validate(options);
        ValidateFallback(options.Fallback);

        this.strategy = options.Strategy ?? ReliabilityPipelineOptions.DefaultStrategy;
        this.policy = options.Policy;
        this.timeProvider = options.TimeProvider;
        this.attemptTimeout = options.AttemptTimeout;
        this.totalTimeout = options.TotalTimeout;
        this.onRetry = options.OnRetry;
        this.circuitBreaker = options.CircuitBreaker;
        this.continueOnCapturedContext = options.ContinueOnCapturedContext;
        this.shouldRetryResult = options.ShouldRetryResult;
        this.fallback = options.Fallback;
        this.hedging = options.Hedging;
        this.hedgingActionGenerator = options.HedgingActionGenerator;
    }

    /// <summary>
    /// Runs an operation, and retries it when it throws an exception or returns a result that is a failure.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    /// The result of the first attempt that succeeds, or the last result if it is a failure and no retries remain. When
    /// the fallback applies, the value of <see cref="FallbackOptions{T}.Action"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses to retry an exception, the exception of the last attempt is rethrown
    /// with its original stack trace. When the strategy refuses to retry a result, the call returns that result.
    /// </para>
    /// <para>
    /// When <see cref="ReliabilityPipelineOptions{T}.Fallback"/> is set, the fallback receives that exception or that result.
    /// It does not receive a cancellation that the caller caused. It does not receive an exception from a callback, the
    /// strategy or a policy of the pipeline: such an exception stops the call. An exception from the fallback stops the
    /// call.
    /// </para>
    /// <para>
    /// The operation returns a <see cref="ValueTask{TResult}"/>. For a method that returns a <see cref="Task{TResult}"/>,
    /// write <c>ct => new ValueTask&lt;T&gt;(GetAsync(ct))</c>.
    /// </para>
    /// </remarks>
    public ValueTask<T> ExecuteAsync(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Hedging replaces the retry part. The fallback stays outside it. The fallback handles only an exception that the
        // engine marks as the outcome of the attempts, so only a call with a fallback needs the marker.
        CallOutcome? outcome = this.fallback is null ? null : new CallOutcome();
        ValueTask<T> core = this.hedging is not null
            ? HedgingEngine.RunAsync(
                operation,
                this.hedgingActionGenerator,
                this.shouldRetryResult,
                this.hedging,
                cancellationToken,
                this.timeProvider,
                this.attemptTimeout,
                this.totalTimeout,
                this.circuitBreaker,
                this.continueOnCapturedContext,
                outcome)
            : RetryEngine.RunAsync<RetryOperations.ResultCheckedOperation<T>, T>(
                new(operation, this.shouldRetryResult),
                cancellationToken,
                this.strategy,
                this.policy,
                this.timeProvider,
                this.attemptTimeout,
                this.totalTimeout,
                this.onRetry,
                this.circuitBreaker,
                this.continueOnCapturedContext,
                outcome);

        // Without a fallback, the call has no extra state machine.
        return this.fallback is null ? core : this.WithFallbackAsync(core, this.fallback, outcome!, cancellationToken);
    }

    private static void ValidateFallback(FallbackOptions<T>? fallback)
    {
        if (fallback is null)
        {
            return;
        }

        if (fallback.Action is null)
        {
            throw new ArgumentNullException(nameof(ReliabilityPipelineOptions<T>.Fallback), "The Action of the fallback must not be null.");
        }

        if (fallback.Policy is null)
        {
            throw new ArgumentNullException(nameof(ReliabilityPipelineOptions<T>.Fallback), "The Policy of the fallback must not be null.");
        }
    }

    // Asks the predicate about a result. An exception from the predicate stops the call. The caller does not get the
    // result, so the pipeline disposes it, and ignores an exception from the disposal.
    private static bool ShouldFallBack(Func<T, bool> shouldFallbackResult, T result)
    {
        try
        {
            return shouldFallbackResult(result);
        }
        catch
        {
            DisposeQuietly(result);
            throw;
        }
    }

    [SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "The call already stops with another exception. That exception is more important to the caller.")]
    private static void DisposeQuietly(T result)
    {
        if (result is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // The call already has an outcome, so the exception has no receiver.
            }
        }
    }

    private async ValueTask<T> WithFallbackAsync(ValueTask<T> core, FallbackOptions<T> fallback, CallOutcome outcome, CancellationToken cancellationToken)
    {
        T result;
        try
        {
            result = await core.ConfigureAwait(this.continueOnCapturedContext);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled, so the fallback does not apply.
            throw;
        }
        catch (Exception ex) when (ReferenceEquals(ex, outcome.Exception))
        {
            // The filter lets an exception from a callback, the strategy or a policy of the pipeline propagate. It is not
            // an outcome of the attempts, so the fallback does not apply.
            // The policy is evaluated here, not in a filter, so that an exception from the policy propagates.
            if (!fallback.Policy.CanRetry(ex))
            {
                throw;
            }

            return await this.FallBackAsync(fallback, new FallbackArgs<T>(ex, default, cancellationToken)).ConfigureAwait(this.continueOnCapturedContext);
        }

        if (fallback.ShouldFallbackResult is not { } shouldFallbackResult || !ShouldFallBack(shouldFallbackResult, result))
        {
            return result;
        }

        // The caller does not get the result, so release it. An exception from the disposal stops the call.
        (result as IDisposable)?.Dispose();
        return await this.FallBackAsync(fallback, new FallbackArgs<T>(null, result, cancellationToken)).ConfigureAwait(this.continueOnCapturedContext);
    }

    private async ValueTask<T> FallBackAsync(FallbackOptions<T> fallback, FallbackArgs<T> args)
    {
        if (fallback.OnFallback is { } onFallback)
        {
            await onFallback(args).ConfigureAwait(this.continueOnCapturedContext);
        }

        return await fallback.Action(args).ConfigureAwait(this.continueOnCapturedContext);
    }
}
