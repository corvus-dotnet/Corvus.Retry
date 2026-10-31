// <copyright file="CircuitBreakerInterruption.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// What happens to a <see cref="CircuitBreaker"/> while a test call is in flight, so that the test call becomes stale.
/// </summary>
public enum CircuitBreakerInterruption
{
    /// <summary>
    /// <see cref="CircuitBreaker.Isolate"/>, then <see cref="CircuitBreaker.Close"/>, then a later break whose duration
    /// ends, then a new test call that is in flight.
    /// </summary>
    IsolateAndClose,

    /// <summary>
    /// <see cref="CircuitBreaker.Close"/> alone, then a later break whose duration ends, then a new test call that is in
    /// flight.
    /// </summary>
    Close,

    /// <summary>
    /// <see cref="CircuitBreaker.Close"/>, then a later break whose duration ends, and no call yet, so the state is still
    /// <see cref="CircuitState.Open"/>.
    /// </summary>
    LaterBreakThatHasEnded,
}
