// <copyright file="IRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// A policy which determines if an operation can be retried, based on the exception raised
    /// when the operation failed.
    /// </summary>
    /// <remarks>
    /// Compare this with <see cref="IRetryStrategy"/> which determines how a retry will be executed.
    /// </remarks>
    public interface IRetryPolicy
    {
        /// <summary>
        /// Determines if the operation can be retried given the exception that was
        /// raised when the operation failed.
        /// </summary>
        /// <param name="exception">The operation that was last raised when the exceptoin failed.</param>
        /// <returns>True if the operation can be retried, otherwise false.</returns>
        bool CanRetry(Exception exception);
    }
}