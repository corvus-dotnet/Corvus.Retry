﻿// <copyright file="Linear.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;

    /// <summary>
    /// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times with a specific delay.
    /// </summary>
    public class Linear : RetryStrategy
    {
        private readonly TimeSpan periodicity;
        private readonly int maxTries;
        private int tryCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="Linear"/> class.
        /// </summary>
        /// <param name="periodicity">The delay between tries.</param>
        /// <param name="maxTries">The maximum number of times the method will be tried.</param>
        public Linear(TimeSpan periodicity, int maxTries)
        {
            if (maxTries <= 0)
            {
                throw new ArgumentException("Max tries must be > 0", nameof(maxTries));
            }

            this.periodicity = periodicity;
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

            return this.periodicity;
        }
    }
}