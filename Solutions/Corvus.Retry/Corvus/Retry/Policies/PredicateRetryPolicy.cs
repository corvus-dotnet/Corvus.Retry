// <copyright file="PredicateRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;

/// <summary>
/// A <see cref="IRetryPolicy"/> that asks a predicate whether to retry.
/// </summary>
/// <remarks>Created by <see cref="RetryPolicies.FromPredicate(Func{Exception, bool})"/>.</remarks>
internal sealed class PredicateRetryPolicy : IRetryPolicy
{
    private readonly Func<Exception, bool> predicate;

    /// <summary>
    /// Initialises a new instance of the <see cref="PredicateRetryPolicy"/> class.
    /// </summary>
    /// <param name="predicate">The predicate that returns true when the operation can be retried.</param>
    internal PredicateRetryPolicy(Func<Exception, bool> predicate)
    {
        this.predicate = predicate;
    }

    /// <inheritdoc/>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return this.predicate(exception);
    }
}
