// <copyright file="AggregatePolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// A <see cref="IRetryPolicy"/> which combines multiple policies.
    /// </summary>
    /// <remarks>
    /// This policy can retry if all the aggregated policies can retry. With no policies, nothing objects to a retry,
    /// so it can retry.
    /// </remarks>
    public class AggregatePolicy : IRetryPolicy
    {
        private List<IRetryPolicy>? policies;

        /// <summary>
        /// Gets the collection of policies to be aggregated.
        /// </summary>
        public List<IRetryPolicy> Policies
        {
            get => this.policies ??= new List<IRetryPolicy>();
        }

        /// <inheritdoc/>
        public bool CanRetry(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return this.Policies.All(p => p.CanRetry(exception));
        }
    }
}