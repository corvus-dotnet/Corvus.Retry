// <copyright file="CancellationCause.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The source that cancelled an attempt.
/// </summary>
internal enum CancellationCause
{
    /// <summary>
    /// No source of the engine was cancelled. The operation cancelled itself, for example after an
    /// <c>HttpClient</c> timeout.
    /// </summary>
    None,

    /// <summary>
    /// The caller's token was cancelled.
    /// </summary>
    Caller,

    /// <summary>
    /// The total timeout of the call expired.
    /// </summary>
    TotalTimeout,

    /// <summary>
    /// The timeout of the attempt expired.
    /// </summary>
    AttemptTimeout,
}
