// <copyright file="IsolatedCircuitException.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The exception that a <see cref="CircuitBreaker"/> throws when it rejects a call because
/// <see cref="CircuitBreaker.Isolate"/> isolated the circuit.
/// </summary>
/// <remarks>The <see cref="BrokenCircuitException.RetryAfter"/> of this exception is always <see langword="null"/>.</remarks>
public sealed class IsolatedCircuitException : BrokenCircuitException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="IsolatedCircuitException"/> class.
    /// </summary>
    public IsolatedCircuitException()
        : this("The circuit is isolated.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="IsolatedCircuitException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public IsolatedCircuitException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="IsolatedCircuitException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception, or <see langword="null"/>.</param>
    public IsolatedCircuitException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
