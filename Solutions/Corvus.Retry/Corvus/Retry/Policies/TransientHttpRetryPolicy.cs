// <copyright file="TransientHttpRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Net;
using System.Net.Http;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries transient HTTP failures.
/// </summary>
/// <remarks>The shared instance is <see cref="RetryPolicies.TransientHttp"/>.</remarks>
internal sealed class TransientHttpRetryPolicy : IRetryPolicy
{
    /// <inheritdoc/>
    /// <remarks>
    /// This accepts an <see cref="HttpRequestException"/> with no status code, or with a transient status code (see
    /// <see cref="IsTransient(HttpStatusCode)"/>), and a <see cref="RetryTimeoutException"/>. It refuses all other
    /// exceptions.
    /// </remarks>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode status } => IsTransient(status),
            HttpRequestException => true,
            RetryTimeoutException => true,
            _ => false,
        };
    }

    /// <summary>
    /// Determines whether an HTTP status code identifies a transient failure.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <returns>True for 408 (Request Timeout), 429 (Too Many Requests), and 500 or more; otherwise false.</returns>
    internal static bool IsTransient(HttpStatusCode status)
    {
        return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    }
}
