// <copyright file="ReliabilityPipelineOptions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The settings of a <see cref="ReliabilityPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// The options are immutable. Use a <see langword="with"/> expression to make a changed copy. The constructor of the
/// pipeline checks the options, and keeps their values, so a pipeline does not read the options again.
/// </para>
/// <para>
/// To make one attempt and no retry, set <see cref="Strategy"/> to <c>new Count(1)</c> or <c>new DoNotRetry()</c>.
/// The <c>maxTries</c> of a strategy is the number of retries plus 1.
/// </para>
/// </remarks>
public record ReliabilityPipelineOptions
{
    /// <summary>
    /// The strategy that a pipeline uses when <see cref="Strategy"/> is <see langword="null"/>: 4 attempts, with 2
    /// seconds before each retry.
    /// </summary>
    internal static readonly IRetryStrategy DefaultStrategy = new Linear(TimeSpan.FromSeconds(2), 4);

    /// <summary>
    /// Gets the strategy that decides whether to retry, and the delay before the retry.
    /// </summary>
    /// <remarks>
    /// When this is <see langword="null"/>, the pipeline makes up to 4 attempts, with 2 seconds before each retry. It
    /// must be <see langword="null"/> when <see cref="Hedging"/> is set.
    /// </remarks>
    public IRetryStrategy? Strategy { get; init; }

    /// <summary>
    /// Gets the policy that decides whether an exception can be retried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. The policy applies only to exceptions, not to results.
    /// A cancellation that the caller caused is never retried.
    /// </para>
    /// <para>
    /// The policy is asked about the exceptions that the operation actually threw. When the operation throws an
    /// <see cref="AggregateException"/>, the policy is asked about each exception inside it, depth first, and the
    /// attempt is retried only when the policy accepts all of them. The strategy receives the exception as it was
    /// thrown.
    /// </para>
    /// </remarks>
    public IRetryPolicy Policy { get; init; } = AnyExceptionPolicy.Default;

    /// <summary>
    /// Gets the time limit for each attempt, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the pipeline cancels the token of the attempt. The timeout is cooperative: the pipeline
    /// waits until the operation stops. The attempt then fails with a <see cref="RetryTimeoutException"/>, which the
    /// policy can retry.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// </remarks>
    public TimeSpan? AttemptTimeout { get; init; }

    /// <summary>
    /// Gets the time limit for all the attempts and all the delays, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the pipeline cancels the token of the attempt, or it stops the delay. No other attempt
    /// starts, and the call throws a <see cref="RetryTimeoutException"/>.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// </remarks>
    public TimeSpan? TotalTimeout { get; init; }

    /// <summary>
    /// Gets a callback that the pipeline awaits before each delay, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback receives the number of the attempt that failed, and the exception or the result that caused the
    /// retry. The delay starts only when the callback completes. An exception from the callback stops the call.
    /// </remarks>
    public Func<RetryEventArgs, ValueTask>? OnRetry { get; init; }

    /// <summary>
    /// Gets the circuit breaker that permits each attempt, or <see langword="null"/> for no circuit breaker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The breaker is inside the retry loop and outside the attempt timeout: it counts each attempt as one call, and an
    /// attempt timeout as a failure. Many pipelines can share one breaker.
    /// </para>
    /// <para>
    /// When the breaker rejects an attempt, the attempt fails with a <see cref="BrokenCircuitException"/>. The strategy
    /// and the policy decide whether to retry it, as for any exception.
    /// </para>
    /// </remarks>
    public CircuitBreaker? CircuitBreaker { get; init; }

    /// <summary>
    /// Gets the settings of hedging, or <see langword="null"/> for no hedging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hedging replaces the retry part of the pipeline: it starts more attempts in parallel when an attempt is slow or
    /// fails, and the first acceptable outcome wins. When this is set, <see cref="Strategy"/> must be
    /// <see langword="null"/>, and the pipeline does not call <see cref="OnRetry"/>.
    /// </para>
    /// <para>
    /// The total timeout applies to all the attempts. Each attempt goes through the circuit breaker and has its own
    /// attempt timeout. To retry and hedge in one call, run one pipeline inside the operation of another pipeline.
    /// </para>
    /// </remarks>
    public HedgingOptions? Hedging { get; init; }

    /// <summary>
    /// Gets the time provider for the delays and the timeouts.
    /// </summary>
    /// <remarks>The default is <see cref="TimeProvider.System"/>. A test can use a fake time provider.</remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets a value indicating whether the pipeline continues on the captured context after each await.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. With <see cref="Hedging"/>, each attempt after the first starts on the
    /// thread pool, so it has no captured context.
    /// </remarks>
    public bool ContinueOnCapturedContext { get; init; }

    /// <summary>
    /// Gets a value indicating whether the options have a setting that only <see cref="ReliabilityPipeline{T}"/> can
    /// use.
    /// </summary>
    internal virtual bool HasResultSettings => false;

    /// <summary>
    /// Checks the options for a pipeline.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="Policy"/>, <see cref="TimeProvider"/> or the
    /// <see cref="HedgingOptions.Policy"/> of <see cref="Hedging"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AttemptTimeout"/> or <see cref="TotalTimeout"/> is not greater than zero, or is greater than the
    /// longest time that a timer supports; or the <see cref="HedgingOptions.MaxHedgedAttempts"/> or the
    /// <see cref="HedgingOptions.Delay"/> of <see cref="Hedging"/> is out of range.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Hedging"/> and <see cref="Strategy"/> are both set; or <paramref name="options"/> is a
    /// <see cref="ReliabilityPipelineOptions{T}"/> with a setting for a result, which only
    /// <see cref="ReliabilityPipeline{T}"/> can use.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not
    /// correct. For a setting for a result, it is <c>options</c>.
    /// </para>
    /// <para>
    /// <see cref="ReliabilityPipeline"/> calls this overload. <see cref="ReliabilityPipeline{T}"/> calls
    /// <see cref="Validate{T}(ReliabilityPipelineOptions{T})"/>: its argument has the type
    /// <see cref="ReliabilityPipelineOptions{T}"/>, so the compiler selects the overload with that more specific
    /// parameter type.
    /// </para>
    /// </remarks>
    internal static void Validate(ReliabilityPipelineOptions options)
    {
        ValidateCore(options);

        if (options.HasResultSettings)
        {
            throw new ArgumentException(
                "The options have settings for a result (ShouldRetryResult, Fallback or HedgingActionGenerator). Use ReliabilityPipeline<T> with these options.",
                nameof(options));
        }
    }

    /// <summary>
    /// Checks the options for a <see cref="ReliabilityPipeline{T}"/>, which can use the settings for a result.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="options">The options.</param>
    /// <remarks>
    /// This makes the same checks as <see cref="Validate(ReliabilityPipelineOptions)"/>, but accepts the settings for
    /// a result.
    /// </remarks>
    internal static void Validate<T>(ReliabilityPipelineOptions<T> options)
    {
        ValidateCore(options);
    }

    private static void ValidateCore(ReliabilityPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfOutOfRange(options.AttemptTimeout, nameof(AttemptTimeout));
        ThrowIfOutOfRange(options.TotalTimeout, nameof(TotalTimeout));
        ArgumentNullException.ThrowIfNull(options.Policy, nameof(Policy));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(TimeProvider));
        ValidateHedging(options);
    }

    private static void ValidateHedging(ReliabilityPipelineOptions options)
    {
        if (options.Hedging is not HedgingOptions hedging)
        {
            return;
        }

        if (options.Strategy is not null)
        {
            throw new ArgumentException("Hedging replaces the retry part of the pipeline, so Strategy must be null when Hedging is set.", nameof(Hedging));
        }

        if (hedging.MaxHedgedAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Hedging),
                hedging.MaxHedgedAttempts,
                "The MaxHedgedAttempts of the hedging must be from 1 thru 10.");
        }

        TimeSpan delay = hedging.Delay;
        if (delay != TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan && (delay < TimeSpan.Zero || delay > RetryDelay.MaxSupported))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Hedging),
                delay,
                $"The Delay of the hedging must be TimeSpan.Zero, Timeout.InfiniteTimeSpan, or greater than zero and not greater than {RetryDelay.MaxSupported}.");
        }

        if (hedging.Policy is null)
        {
            throw new ArgumentNullException(nameof(Hedging), "The Policy of the hedging must not be null.");
        }
    }

    private static void ThrowIfOutOfRange(TimeSpan? timeout, string propertyName)
    {
        // This also rejects Timeout.InfiniteTimeSpan, which is negative.
        if (timeout is TimeSpan value && (value <= TimeSpan.Zero || value > RetryDelay.MaxSupported))
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                $"The timeout must be greater than zero and not greater than {RetryDelay.MaxSupported}. Use null for no timeout.");
        }
    }
}
