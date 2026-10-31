// <copyright file="FallbackEventArgs{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;

/// <summary>
/// The cause of a fallback, which <see cref="Fallback{T}.OnFallback"/> and <see cref="Fallback{T}.Action"/> receive.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <param name="Exception">
/// The exception that caused the fallback, or <see langword="null"/> if a result caused it.
/// </param>
/// <param name="Result">
/// The result that caused the fallback, or the default value if an exception caused it. When
/// <see cref="Fallback{T}.OnFallback"/> receives it, the factory has not yet disposed it, so the callback can examine
/// it. The factory then disposes a result that implements <see cref="IDisposable"/>, so
/// <see cref="Fallback{T}.Action"/> receives a result that is already disposed.
/// </param>
/// <param name="CancellationToken">
/// The caller's token. The total timeout of the factory does not apply to the fallback.
/// </param>
public readonly record struct FallbackEventArgs<T>(Exception? Exception, T? Result, CancellationToken CancellationToken);
