// <copyright file="RetryPolicyExtensions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Extension members that combine <see cref="IRetryPolicy"/> instances.
/// </summary>
/// <remarks>
/// Every combination is a new, immutable policy. The policies that it combines do not change.
/// </remarks>
[SuppressMessage(
    "StyleCop.CSharp.ReadabilityRules",
    "SA1101:PrefixLocalCallsWithThis",
    Justification = "StyleCop 1.2.0-beta.556 treats the receiver parameter of a C# 14 extension block as a member. A static class has no this.")]
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Extension members for an <see cref="IRetryPolicy"/>.
    /// </summary>
    /// <param name="policy">The policy.</param>
    extension(IRetryPolicy policy)
    {
        /// <summary>
        /// Creates a policy that retries an exception that this policy or the other policy accepts.
        /// </summary>
        /// <param name="other">The other policy.</param>
        /// <returns>The policy. It asks this policy first.</returns>
        /// <exception cref="ArgumentNullException">The policy or <paramref name="other"/> is <see langword="null"/>.</exception>
        public IRetryPolicy Or(IRetryPolicy other)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(other);

            return new AnyRetryPolicy([policy, other]);
        }

        /// <summary>
        /// Creates a policy that retries an exception that this policy and the other policy both accept.
        /// </summary>
        /// <param name="other">The other policy.</param>
        /// <returns>The policy. It asks this policy first.</returns>
        /// <exception cref="ArgumentNullException">The policy or <paramref name="other"/> is <see langword="null"/>.</exception>
        public IRetryPolicy And(IRetryPolicy other)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(other);

            return new AllRetryPolicy([policy, other]);
        }

        /// <summary>
        /// Creates a policy that retries an exception that this policy refuses.
        /// </summary>
        /// <returns>The policy.</returns>
        /// <exception cref="ArgumentNullException">The policy is <see langword="null"/>.</exception>
        public IRetryPolicy Not()
        {
            return RetryPolicies.Not(policy);
        }
    }

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
