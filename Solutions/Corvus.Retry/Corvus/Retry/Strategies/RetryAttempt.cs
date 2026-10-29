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
/// The number of the attempt that failed. The first attempt is 1.
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
public readonly record struct RetryAttempt(int AttemptNumber, Exception? Exception, object? Result, TimeSpan PreviousDelay);
