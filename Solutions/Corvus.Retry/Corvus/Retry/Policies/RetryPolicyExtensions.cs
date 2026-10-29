// <copyright file="RetryPolicyExtensions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.ObjectModel;

/// <summary>
/// Helpers for consulting an <see cref="IRetryPolicy"/> about the failure of a task.
/// </summary>
internal static class RetryPolicyExtensions
{
    /// <summary>
    /// Determines whether the policy allows a retry after a task faulted with the given exception.
    /// </summary>
    /// <param name="policy">The policy to consult.</param>
    /// <param name="exception">The task's exception.</param>
    /// <returns>True if the policy accepts every individual failure, otherwise false.</returns>
    /// <remarks>
    /// <para>
    /// The policy is asked about the exceptions the operation actually threw, not the <see cref="AggregateException"/>
    /// wrappers around them, in depth-first order, stopping at the first it rejects. An
    /// <see cref="AggregateException"/> with no inner exceptions is itself the failure, so the policy is always
    /// consulted at least once.
    /// </para>
    /// <para>
    /// This uses plain loops and recursion rather than iterators or LINQ, so that it allocates nothing.
    /// </para>
    /// </remarks>
    internal static bool CanRetryAll(this IRetryPolicy policy, AggregateException exception)
    {
        ReadOnlyCollection<Exception> inner = exception.InnerExceptions;
        if (inner.Count == 0)
        {
            return policy.CanRetry(exception);
        }

        for (int i = 0; i < inner.Count; i++)
        {
            bool canRetry = inner[i] is AggregateException aggregate
                ? policy.CanRetryAll(aggregate)
                : policy.CanRetry(inner[i]);

            if (!canRetry)
            {
                return false;
            }
        }

        return true;
    }
}
