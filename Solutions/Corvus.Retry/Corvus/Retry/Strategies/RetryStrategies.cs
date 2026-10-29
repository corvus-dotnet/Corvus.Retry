// <copyright file="RetryStrategies.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

/// <summary>
/// Provides shared <see cref="IRetryStrategy"/> instances.
/// </summary>
public static class RetryStrategies
{
    /// <summary>
    /// Gets the strategy that the overloads without a strategy parameter use: up to 10 tries, without delay.
    /// </summary>
    /// <remarks>This is one shared <see cref="Count"/> instance. Strategies hold no state for a call, so it is safe to share.</remarks>
    public static IRetryStrategy Default { get; } = new Count(10);
}
