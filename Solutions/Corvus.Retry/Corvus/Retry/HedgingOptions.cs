// <copyright file="HedgingOptions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The settings of hedging that <see cref="HedgingEngine"/> takes.
/// </summary>
/// <remarks>
/// <see cref="HedgedTaskFactory"/> and <see cref="HedgedTaskFactory{T}"/> make an instance for each call from their own
/// settings, which they have already checked. The public documentation of each setting is on those factories.
/// </remarks>
internal sealed record HedgingOptions
{
    /// <summary>
    /// Gets the largest number of attempts that the engine starts after the first attempt.
    /// </summary>
    /// <remarks>This is <see cref="HedgedTaskFactory.ExtraAttempts"/>.</remarks>
    public int MaxHedgedAttempts { get; init; } = 1;

    /// <summary>
    /// Gets the time after the start of an attempt at which the engine starts the next attempt, if no outcome has won.
    /// </summary>
    /// <remarks>This is <see cref="HedgedTaskFactory.Stagger"/>.</remarks>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the policy that decides whether an exception from an attempt is a failure.
    /// </summary>
    /// <remarks>This is <see cref="HedgedTaskFactory.Policy"/>.</remarks>
    public IRetryPolicy Policy { get; init; } = AnyExceptionPolicy.Default;

    /// <summary>
    /// Gets a callback that the engine awaits before it starts each attempt after the first, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>This is <see cref="HedgedTaskFactory.OnHedgedAttempt"/>.</remarks>
    public Func<HedgedAttemptEventArgs, ValueTask>? OnHedging { get; init; }
}
