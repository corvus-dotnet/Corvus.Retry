// <copyright file="AllRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries an exception that all of its policies accept.
/// </summary>
/// <remarks>
/// Created by <see cref="RetryPolicies.All(ReadOnlySpan{IRetryPolicy})"/>. With no policies, nothing objects to a
/// retry, so it retries. This gives the same result as <see cref="AggregatePolicy"/>, but cannot change.
/// </remarks>
internal sealed class AllRetryPolicy : IRetryPolicy
{
    private readonly IRetryPolicy[] policies;

    /// <summary>
    /// Initialises a new instance of the <see cref="AllRetryPolicy"/> class.
    /// </summary>
    /// <param name="policies">The policies. This instance owns the array.</param>
    internal AllRetryPolicy(IRetryPolicy[] policies)
    {
        this.policies = policies;
    }

    /// <inheritdoc/>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (IRetryPolicy policy in this.policies)
        {
            if (!policy.CanRetry(exception))
            {
                return false;
            }
        }

        return true;
    }
}
