// <copyright file="HedgedAttemptEventArgs.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// The cause of a hedged attempt, which <see cref="HedgedTaskFactory.OnHedgedAttempt"/> and
/// <see cref="HedgedTaskFactory{T}.OnHedgedAttempt"/> receive.
/// </summary>
/// <param name="AttemptNumber">The number of the attempt that is about to start. The first hedged attempt is 2.</param>
/// <param name="LastException">
/// The exception that caused the attempt, or <see langword="null"/> when the stagger expired or when a result was a
/// failure.
/// </param>
public readonly record struct HedgedAttemptEventArgs(int AttemptNumber, Exception? LastException);
