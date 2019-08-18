// <copyright file="RetryEventArgs.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// Event data for a retry event.
    /// </summary>
    /// <remarks>
    /// This is used by the <see cref="IRetryStrategy.Retrying"/> event, and raised by the <see cref="Retriable"/> mechanism.
    /// </remarks>
    public class RetryEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RetryEventArgs"/> class.
        /// </summary>
        /// <param name="lastException">The last exception that occurred.</param>
        /// <param name="delayBeforeRetry">The delay before the next retrying.</param>
        public RetryEventArgs(Exception lastException, TimeSpan delayBeforeRetry)
        {
            this.LastException = lastException;
            this.DelayBeforeRetry = delayBeforeRetry;
        }

        /// <summary>
        /// Gets the delay before the next retry.
        /// </summary>
        public TimeSpan DelayBeforeRetry { get; }

        /// <summary>
        /// Gets the last exception that occurred.
        /// </summary>
        public Exception LastException { get; }
    }
}