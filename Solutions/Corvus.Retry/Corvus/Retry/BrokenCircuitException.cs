// <copyright file="BrokenCircuitException.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The exception that a <see cref="CircuitBreaker"/> throws when it rejects a call.
/// </summary>
/// <remarks>
/// The breaker rejects a call when the circuit is open, or when it is half-open and a test call is in progress. A retry
/// strategy and a retry policy see this exception as a failure of the attempt.
/// </remarks>
public class BrokenCircuitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BrokenCircuitException"/> class.
    /// </summary>
    public BrokenCircuitException()
        : this("The circuit is open.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BrokenCircuitException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BrokenCircuitException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BrokenCircuitException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception, or <see langword="null"/>.</param>
    public BrokenCircuitException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BrokenCircuitException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="retryAfter">The time until the break ends.</param>
    public BrokenCircuitException(string message, TimeSpan retryAfter)
        : base(message)
    {
        this.RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the time until the break ends, or <see langword="null"/> if it is not known.
    /// </summary>
    /// <remarks>
    /// When the circuit is open, this is the time that remains in the break. When the circuit is half-open or isolated,
    /// this is <see langword="null"/>.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }
}
