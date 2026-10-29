// <copyright file="CircuitState.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The states of a <see cref="CircuitBreaker"/>.
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// The breaker lets calls through, and counts the calls and the failures.
    /// </summary>
    Closed,

    /// <summary>
    /// The breaker rejects each call with a <see cref="BrokenCircuitException"/> until the break ends.
    /// </summary>
    Open,

    /// <summary>
    /// The break has ended. The breaker lets one test call through, and rejects the other calls.
    /// </summary>
    HalfOpen,

    /// <summary>
    /// The breaker rejects each call with an <see cref="IsolatedCircuitException"/> until
    /// <see cref="CircuitBreaker.Close"/> is called.
    /// </summary>
    Isolated,
}
