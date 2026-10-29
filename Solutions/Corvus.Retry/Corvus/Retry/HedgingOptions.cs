// <copyright file="HedgingOptions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The settings of hedging: a pipeline starts more attempts in parallel when an attempt is slow or fails, and the
/// first acceptable outcome wins.
/// </summary>
/// <remarks>
/// <para>
/// Hedging replaces the retry part of a pipeline. When <see cref="ReliabilityPipelineOptions.Hedging"/> is set,
/// <see cref="ReliabilityPipelineOptions.Strategy"/> must be <see langword="null"/>, and the pipeline does not call
/// <see cref="ReliabilityPipelineOptions.OnRetry"/>.
/// </para>
/// <para>
/// The operation must be idempotent. It must also be safe when two or more attempts run at the same time.
/// </para>
/// <para>
/// The options are immutable. The constructor of the pipeline checks them.
/// </para>
/// </remarks>
public sealed record HedgingOptions
{
    /// <summary>
    /// Gets the largest number of attempts that the pipeline starts after the first attempt.
    /// </summary>
    /// <remarks>
    /// The default is 1. The value must be from 1 thru 10. The total number of attempts is
    /// <see cref="MaxHedgedAttempts"/> plus 1.
    /// </remarks>
    public int MaxHedgedAttempts { get; init; } = 1;

    /// <summary>
    /// Gets the time after the start of an attempt at which the pipeline starts the next attempt, if no outcome has
    /// won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is 2 seconds. The delay starts again each time an attempt starts. When an attempt fails, the next
    /// attempt starts immediately.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> starts all the attempts at once. <see cref="Timeout.InfiniteTimeSpan"/> starts the
    /// next attempt only after a failure. Any other value must be greater than zero and not greater than about 49.7
    /// days.
    /// </para>
    /// </remarks>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the policy that decides whether an exception from an attempt is a failure.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. A failure starts the next attempt. An exception that
    /// the policy refuses is not a failure: the pipeline throws it immediately, and cancels the other attempts.
    /// </remarks>
    public IRetryPolicy Policy { get; init; } = AnyExceptionPolicy.Default;

    /// <summary>
    /// Gets a callback that the pipeline awaits before it starts each attempt after the first, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback receives the number of the attempt that is about to start, and the exception that caused it. The
    /// exception is <see langword="null"/> when the delay expired or when a result was a failure. An exception from the
    /// callback stops the call.
    /// </remarks>
    public Func<HedgingEventArgs, ValueTask>? OnHedging { get; init; }
}
