// <copyright file="AnyExceptionPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;

    /// <summary>
    /// A <see cref="IRetryPolicy"/> that will always retry regardless of the exception.
    /// </summary>
    public class AnyExceptionPolicy : IRetryPolicy
    {
        /// <summary>
        /// Gets a shared instance, used by the overloads that default the policy. The policy has no state, so one
        /// instance serves every caller.
        /// </summary>
        internal static AnyExceptionPolicy Default { get; } = new AnyExceptionPolicy();

        /// <inheritdoc/>
        public bool CanRetry(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return true;
        }
    }
}