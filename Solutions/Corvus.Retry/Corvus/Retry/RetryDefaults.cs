// <copyright file="RetryDefaults.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// Process-wide defaults for the APIs that have no options object: <see cref="Retriable"/> and
/// <see cref="RetryTaskFactory"/>.
/// </summary>
internal static class RetryDefaults
{
    /// <summary>
    /// Gets or sets the time provider with which <see cref="Retriable"/> and <see cref="RetryTaskFactory"/> wait
    /// between retries.
    /// </summary>
    /// <remarks>
    /// This is read on every call. Only tests replace it; a test that does must not run in parallel with other tests,
    /// and must restore <see cref="TimeProvider.System"/> when it ends.
    /// </remarks>
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
