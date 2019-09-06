// <copyright file="Count.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;

    /// <summary>
    /// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times without delay.
    /// </summary>
    public class Count : RetryStrategy
    {
        private readonly int maxTries;
        private int tryCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="Count"/> class.
        /// </summary>
        /// <remarks>The will retry up to 5 times.</remarks>
        public Count()
            : this(5)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Count"/> class.
        /// </summary>
        /// <param name="maxTries">The maximum number of times it will attempt to retry.</param>
        public Count(int maxTries)
        {
            if (maxTries <= 0)
            {
                throw new ArgumentException("Max tries must be > 0", nameof(maxTries));
            }

            this.maxTries = maxTries;
        }

        /// <inheritdoc/>
        public override bool CanRetry
        {
            get
            {
                return this.tryCount < this.maxTries;
            }
        }

        /// <inheritdoc/>
        public override TimeSpan PrepareToRetry(Exception lastException)
        {
            if (lastException is null)
            {
                throw new ArgumentNullException(nameof(lastException));
            }

            this.AddException(lastException);

            this.tryCount++;

            return TimeSpan.Zero;
        }
    }
}