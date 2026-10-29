// <copyright file="NotRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries an exception that another policy refuses.
/// </summary>
/// <remarks>Created by <see cref="RetryPolicies.Not(IRetryPolicy)"/>.</remarks>
internal sealed class NotRetryPolicy : IRetryPolicy
{
    private readonly IRetryPolicy policy;

    /// <summary>
    /// Initialises a new instance of the <see cref="NotRetryPolicy"/> class.
    /// </summary>
    /// <param name="policy">The policy to negate.</param>
    internal NotRetryPolicy(IRetryPolicy policy)
    {
        this.policy = policy;
    }

    /// <inheritdoc/>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return !this.policy.CanRetry(exception);
    }
}
