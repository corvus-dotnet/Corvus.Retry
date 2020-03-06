// <copyright file="RetryStrategy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A base class for implementers of <see cref="IRetryStrategy"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementers override <see cref="PrepareToRetry(System.Exception)"/> and <see cref="CanRetry"/>.
    /// </para>
    /// <para>
    /// They can make use of the standard <see cref="OnRetrying(RetryEventArgs)"/> implementation and the <see cref="AddException(System.Exception)"/> method
    /// to flatten exceptions into the list.
    /// </para>
    /// </remarks>
    public abstract class RetryStrategy : IRetryStrategy
    {
        private readonly List<Exception> exceptions = new List<Exception>();

        /// <inheritdoc/>
        public event EventHandler<RetryEventArgs>? Retrying;

        /// <inheritdoc/>
        public AggregateException Exception
        {
            get
            {
                return new AggregateException(this.exceptions);
            }
        }

        /// <inheritdoc/>
        public abstract bool CanRetry
        {
            get;
        }

        /// <inheritdoc/>
        public abstract TimeSpan PrepareToRetry(Exception lastException);

        /// <inheritdoc/>
        public void OnRetrying(RetryEventArgs eventArgs)
        {
            this.Retrying?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Adds the provided exception to the list of exceptions that have occurred.
        /// </summary>
        /// <param name="exception">The exception to add.</param>
        /// <remarks>This will flatten an aggregate exception into the list.</remarks>
        protected void AddException(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (exception is AggregateException aggregateException)
            {
                foreach (Exception ex in aggregateException.InnerExceptions)
                {
                    this.exceptions.Add(ex);
                }
            }
            else
            {
                this.exceptions.Add(exception);
            }
        }
    }
}