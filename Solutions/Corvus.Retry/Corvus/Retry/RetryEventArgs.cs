// <copyright file="RetryEventArgs.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// Event data for a retry event.
/// </summary>
/// <remarks>
/// This describes an attempt that failed, and the retry that will follow it.
/// </remarks>
public class RetryEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RetryEventArgs"/> class.
    /// </summary>
    /// <param name="lastException">
    /// The exception that the attempt threw, or <see langword="null"/> when a result caused the retry.
    /// </param>
    /// <param name="result">
    /// The result that caused the retry, or <see langword="null"/> when an exception caused the retry.
    /// </param>
    /// <param name="delayBeforeRetry">The delay before the next attempt.</param>
    /// <param name="attemptNumber">The number of the attempt that failed. The first attempt is 1.</param>
    public RetryEventArgs(Exception? lastException, object? result, TimeSpan delayBeforeRetry, int attemptNumber)
    {
        this.LastException = lastException;
        this.Result = result;
        this.DelayBeforeRetry = delayBeforeRetry;
        this.AttemptNumber = attemptNumber;
    }

    /// <summary>
    /// Gets the delay before the next attempt.
    /// </summary>
    public TimeSpan DelayBeforeRetry { get; }

    /// <summary>
    /// Gets the exception that the attempt threw, or <see langword="null"/> when a result caused the retry.
    /// </summary>
    public Exception? LastException { get; }

    /// <summary>
    /// Gets the result that caused the retry, or <see langword="null"/> when an exception caused the retry.
    /// </summary>
    public object? Result { get; }

    /// <summary>
    /// Gets the number of the attempt that failed. The first attempt is 1.
    /// </summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// Gets the number of the attempt that will run after the delay.
    /// </summary>
    public int NextAttemptNumber => this.AttemptNumber + 1;
}
