// <copyright file="CircuitIsolatedException.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The exception that a <see cref="CircuitBreaker"/> throws when it rejects a call because
/// <see cref="CircuitBreaker.Isolate"/> isolated the circuit.
/// </summary>
/// <remarks>The <see cref="CircuitRejectedException.RetryAfter"/> of this exception is always <see langword="null"/>.</remarks>
public sealed class CircuitIsolatedException : CircuitRejectedException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitIsolatedException"/> class.
    /// </summary>
    public CircuitIsolatedException()
        : this("The circuit is isolated.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitIsolatedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CircuitIsolatedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitIsolatedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception, or <see langword="null"/>.</param>
    public CircuitIsolatedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
