// <copyright file="DoNotRetry.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;

    /// <summary>
    /// Provides a <see cref="IRetryStrategy"/> which will always stop retrying immediately.
    /// </summary>
    public class DoNotRetry : RetryStrategy
    {
        /// <inheritdoc/>
        public override bool CanRetry
        {
            get { return false; }
        }

        /// <inheritdoc/>
        public override TimeSpan PrepareToRetry(Exception lastException)
        {
            return TimeSpan.Zero;
        }
    }
}