// <copyright file="RetryTimeoutException.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The exception that is thrown when a time limit for an operation expires.
/// </summary>
/// <remarks>
/// The inner exception is the <see cref="OperationCanceledException"/> that the expired time limit caused. When the
/// total timeout expires between attempts, it is the exception of the last attempt. It can also be
/// <see langword="null"/>.
/// </remarks>
public sealed class RetryTimeoutException : TimeoutException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTimeoutException"/> class.
    /// </summary>
    /// <param name="timeout">The time limit that expired.</param>
    public RetryTimeoutException(TimeSpan timeout)
        : this(timeout, null)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTimeoutException"/> class.
    /// </summary>
    /// <param name="timeout">The time limit that expired.</param>
    /// <param name="innerException">
    /// The <see cref="OperationCanceledException"/> that the expired time limit caused, the exception of the last attempt
    /// when the total timeout expired between attempts, or <see langword="null"/>.
    /// </param>
    public RetryTimeoutException(TimeSpan timeout, Exception? innerException)
        : this($"The operation did not complete within {timeout}.", timeout, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTimeoutException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="timeout">The time limit that expired.</param>
    /// <param name="innerException">The exception that the expired time limit caused, or <see langword="null"/>.</param>
    public RetryTimeoutException(string message, TimeSpan timeout, Exception? innerException)
        : base(message, innerException)
    {
        this.Timeout = timeout;
    }

    /// <summary>
    /// Gets the time limit that expired.
    /// </summary>
    public TimeSpan Timeout { get; }
}