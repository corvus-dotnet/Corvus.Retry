// <copyright file="IRetryStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using Corvus.Retry.Policies;

    /// <summary>
    /// A strategy which determines how an operation will be retried
    /// when the operation failed.
    /// </summary>
    /// <remarks>
    /// Compare this with <see cref="IRetryPolicy"/> which determines whether a retry will be executed.
    /// </remarks>
    public interface IRetryStrategy
    {
        /// <summary>
        /// An event raised when an operation is about to be retried
        /// </summary>
        event EventHandler<RetryEventArgs> Retrying;

        /// <summary>
        /// Gets the exceptions associated with previous failures of the operation.
        /// </summary>
        AggregateException Exception { get; }

        /// <summary>
        /// Gets a value indicating whether the operation can currently be retried.
        /// </summary>
        bool CanRetry { get; }

        /// <summary>
        /// Called when a Task has failed, and is about to
        /// be retried.
        /// </summary>
        /// <param name="lastException">The last exception seen.</param>
        /// <returns>The time to delay before retrying.</returns>
        TimeSpan PrepareToRetry(Exception lastException);

        /// <summary>
        /// Raises the <see cref="Retrying"/> event.
        /// </summary>
        /// <param name="eventArgs">The event data.</param>
        void OnRetrying(RetryEventArgs eventArgs);
    }
}