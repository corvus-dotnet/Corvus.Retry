// <copyright file="CircuitRejectedException.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The exception that a <see cref="CircuitBreaker"/> throws when it rejects a call.
/// </summary>
/// <remarks>
/// <para>
/// The breaker rejects a call when the circuit is open, or when it is half-open and a test call is in progress. When
/// <see cref="CircuitBreaker.Isolate"/> isolated the circuit, the breaker throws the derived
/// <see cref="CircuitIsolatedException"/>.
/// </para>
/// <para>
/// A retry strategy and a retry policy see this exception as a failure of the attempt.
/// </para>
/// </remarks>
public class CircuitRejectedException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitRejectedException"/> class.
    /// </summary>
    public CircuitRejectedException()
        : this("The circuit breaker rejected the call.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CircuitRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception, or <see langword="null"/>.</param>
    public CircuitRejectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="retryAfter">The time until the circuit stops being open.</param>
    public CircuitRejectedException(string message, TimeSpan retryAfter)
        : base(message)
    {
        this.RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the time until the circuit stops being open, or <see langword="null"/> if it is not known.
    /// </summary>
    /// <remarks>
    /// When the circuit is open, this is the time that remains before the breaker lets a test call through. When the
    /// circuit is half-open or isolated, this is <see langword="null"/>.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }
}
