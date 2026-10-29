// <copyright file="AnyRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries an exception that one or more of its policies accept.
/// </summary>
/// <remarks>
/// Created by <see cref="RetryPolicies.Any(ReadOnlySpan{IRetryPolicy})"/>. With no policies, it never retries.
/// </remarks>
internal sealed class AnyRetryPolicy : IRetryPolicy
{
    private readonly IRetryPolicy[] policies;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnyRetryPolicy"/> class.
    /// </summary>
    /// <param name="policies">The policies. This instance owns the array.</param>
    internal AnyRetryPolicy(IRetryPolicy[] policies)
    {
        this.policies = policies;
    }

    /// <inheritdoc/>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (IRetryPolicy policy in this.policies)
        {
            if (policy.CanRetry(exception))
            {
                return true;
            }
        }

        return false;
    }
}
