// <copyright file="RetryEventArgs.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The data for a retry, which an <c>onRetry</c> callback receives.
/// </summary>
/// <param name="LastException">
/// The exception that the attempt threw, or <see langword="null"/> when a result caused the retry.
/// </param>
/// <param name="Result">
/// The result that caused the retry, or <see langword="null"/> when an exception caused the retry.
/// </param>
/// <param name="DelayBeforeRetry">The delay before the next attempt.</param>
/// <param name="AttemptNumber">The number of the attempt that failed. The first attempt is 1.</param>
/// <remarks>
/// This describes an attempt that failed, and the retry that will follow it. It is a value type, so a retry does not
/// allocate it.
/// </remarks>
public readonly record struct RetryEventArgs(Exception? LastException, object? Result, TimeSpan DelayBeforeRetry, int AttemptNumber)
{
    /// <summary>
    /// Gets the number of the attempt that will run after the delay.
    /// </summary>
    public int NextAttemptNumber => this.AttemptNumber + 1;
}
