// <copyright file="RetryAttempt.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;

/// <summary>
/// Describes an attempt that failed, so that an <see cref="IRetryStrategy"/> can decide whether to retry, and the
/// delay before the retry.
/// </summary>
/// <param name="AttemptNumber">
/// The number of the attempt that failed. The first attempt is 1. A number less than 1 throws
/// <see cref="ArgumentOutOfRangeException"/>.
/// </param>
/// <param name="Exception">
/// The exception that the attempt threw, or <see langword="null"/> when a result caused the retry.
/// </param>
/// <param name="Result">
/// The result that caused the retry, or <see langword="null"/> when an exception caused the retry.
/// </param>
/// <param name="PreviousDelay">
/// The delay before the attempt that failed, or <see cref="TimeSpan.Zero"/> for the first attempt.
/// </param>
/// <remarks>
/// The constructor and a <see langword="with"/> expression reject an attempt number less than 1, so a strategy never
/// receives one from them. The <see langword="default"/> value of this type has an attempt number of 0, and does not
/// describe an attempt.
/// </remarks>
public readonly record struct RetryAttempt(int AttemptNumber, Exception? Exception, object? Result, TimeSpan PreviousDelay)
{
    /// <summary>
    /// Gets the number of the attempt that failed. The first attempt is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int AttemptNumber { get; init => field = Validate(value); } = Validate(AttemptNumber);

    private static int Validate(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1, nameof(AttemptNumber));
        return attemptNumber;
    }
}
