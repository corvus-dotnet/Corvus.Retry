// <copyright file="CircuitBreakerExit.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The ways in which a call that a <see cref="CircuitBreaker"/> permits can end. Each way must report exactly one
/// outcome to the breaker: a success, a failure, or no outcome.
/// </summary>
public enum CircuitBreakerExit
{
    /// <summary>The operation returns a result that is not a failure.</summary>
    Success,

    /// <summary>The operation throws an exception that the policy of the breaker accepts.</summary>
    Failure,

    /// <summary>The operation returns a result that <see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/> rejects.</summary>
    ResultRejected,

    /// <summary><see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/> throws.</summary>
    ResultCheckThrows,

    /// <summary>The attempt timeout expires during the operation.</summary>
    AttemptTimeout,

    /// <summary>The total timeout expires during the operation.</summary>
    TotalTimeout,

    /// <summary>The operation throws an exception that the policy of the breaker refuses.</summary>
    PolicyRefuses,

    /// <summary>The policy of the breaker throws when the breaker asks it about the exception of the operation.</summary>
    PolicyThrows,

    /// <summary>The operation throws an <see cref="System.OperationCanceledException"/> for a token that is not the caller's.</summary>
    OperationCancelsItself,

    /// <summary>The caller cancels the call.</summary>
    CallerCancels,
}
