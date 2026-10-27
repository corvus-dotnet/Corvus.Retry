// <copyright file="RetryPolicyExtensions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

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
        /// The policy is asked about the exceptions the operation actually threw, not the <see cref="AggregateException"/>
        /// wrappers around them. An <see cref="AggregateException"/> with no inner exceptions is itself the failure, so
        /// the policy is always consulted at least once.
        /// </remarks>
        internal static bool CanRetryAll(this IRetryPolicy policy, AggregateException exception)
        {
            return Failures(exception).All(policy.CanRetry);
        }

        private static IEnumerable<Exception> Failures(Exception exception)
        {
            if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    foreach (Exception failure in Failures(inner))
                    {
                        yield return failure;
                    }
                }
            }
            else
            {
                yield return exception;
            }
        }
    }
}
