// <copyright file="Incremental.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;

    /// <summary>
    /// Provides a <see cref="IRetryStrategy"/> which retries a specific number of times with a precisely incrementing delay.
    /// </summary>
    /// <remarks>
    /// Compare this with <see cref="Backoff"/>, which introduces a random element to the delay to help break contended resources scenarios.
    /// </remarks>
    public class Incremental : RetryStrategy
    {
        private readonly int maxTries;
        private readonly TimeSpan step;
        private readonly TimeSpan initialDelay;
        private int tryCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="Incremental"/> class.
        /// </summary>
        /// <remarks>This retries 5 times by default, with an initial delay of 1 second and a step of 2 seconds.</remarks>
        public Incremental()
            : this(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Incremental"/> class.
        /// </summary>
        /// <param name="maxTries">The maximum number of retries.</param>
        /// <param name="intialDelay">The initial delay.</param>
        /// <param name="step">The increment step per retry.</param>
        public Incremental(int maxTries, TimeSpan intialDelay, TimeSpan step)
        {
            this.maxTries = maxTries;
            this.initialDelay = intialDelay;
            this.step = step;
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
            this.AddException(lastException);

            this.tryCount++;

            if (this.CanRetry)
            {
                double delay = ((this.tryCount - 1) * this.step.TotalMilliseconds) + this.initialDelay.TotalMilliseconds;

                return TimeSpan.FromMilliseconds(delay);
            }

            return TimeSpan.Zero;
        }
    }
}