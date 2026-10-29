// <copyright file="RetryPipelineOptions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The settings of a <see cref="RetryPipeline"/>.
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
public record RetryPipelineOptions
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
    /// When this is <see langword="null"/>, the pipeline makes up to 4 attempts, with 2 seconds before each retry.
    /// </remarks>
    public IRetryStrategy? Strategy { get; init; }

    /// <summary>
    /// Gets the policy that decides whether an exception can be retried.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. The policy applies only to exceptions, not to results.
    /// A cancellation that the caller caused is never retried.
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
    /// Gets the time provider for the delays and the timeouts.
    /// </summary>
    /// <remarks>The default is <see cref="TimeProvider.System"/>. A test can use a fake time provider.</remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets a value indicating whether the pipeline continues on the captured context after each await.
    /// </summary>
    /// <remarks>The default is <see langword="false"/>.</remarks>
    public bool ContinueOnCapturedContext { get; init; }

    /// <summary>
    /// Checks the options for a pipeline.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="Policy"/> or <see cref="TimeProvider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AttemptTimeout"/> or <see cref="TotalTimeout"/> is not greater than zero, or is greater than the
    /// longest time that a timer supports.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not
    /// correct.
    /// </remarks>
    internal static void Validate(RetryPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfOutOfRange(options.AttemptTimeout, nameof(AttemptTimeout));
        ThrowIfOutOfRange(options.TotalTimeout, nameof(TotalTimeout));
        ArgumentNullException.ThrowIfNull(options.Policy, nameof(Policy));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(TimeProvider));
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
