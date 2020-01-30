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