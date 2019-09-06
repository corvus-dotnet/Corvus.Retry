// <copyright file="DoNotRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;

    /// <summary>
    /// A retry policy which never retries.
    /// </summary>
    public class DoNotRetryPolicy : IRetryPolicy
    {
        /// <inheritdoc/>
        public bool CanRetry(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return false;
        }
    }
}