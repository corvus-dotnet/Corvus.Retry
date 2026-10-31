// <copyright file="CallOutcome.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// Records the exception that a call throws as the outcome of its attempts.
/// </summary>
/// <remarks>
/// <para>
/// An engine sets <see cref="Exception"/> immediately before it throws an exception that is the outcome of the
/// attempts: the exception of the last attempt, a <see cref="RetryTimeoutException"/>, or a
/// <see cref="BrokenCircuitException"/>. It does not set it for an exception from a callback, a strategy or a policy.
/// </para>
/// <para>
/// The fallback of <see cref="ReliabilityPipeline{T}"/> compares the exception that it catches with this value. It
/// handles only the outcome of the attempts. A pipeline makes an instance only when it has a fallback.
/// </para>
/// </remarks>
internal sealed class CallOutcome
{
    /// <summary>
    /// Gets or sets the exception that the call throws as the outcome of its attempts, or <see langword="null"/>.
    /// </summary>
    public Exception? Exception { get; set; }
}
