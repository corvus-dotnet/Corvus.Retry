// <copyright file="Backoff.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;

    /// <summary>
    /// Provides a <see cref="IRetryStrategy"/> which increases the delay between retries on each occurrence.
    /// </summary>
    /// <remarks>
    /// <para>The delay is actually by a slightly randomized amount within +/-20% of a specific delta . You can cap the minimum and maximum values using <see cref="MinBackoff"/> and <see cref="MaxBackoff"/>.</para>
    /// <para>Compare this with <see cref="Incremental"/>, which provides a simply incrementing delay, and is therefore prone to contended resource race conditions.</para>
    /// </remarks>
    public class Backoff : RetryStrategy
    {
#if !NET6_0_OR_GREATER
        // Seeds each thread's generator. On .NET Framework, Random instances created close together share a time-based
        // seed, so clients failing together would calculate the same "random" delay and retry in lockstep.
        private static readonly Random Seeds = new();

        [ThreadStatic]
        private static Random? threadRandom;
#endif

        private readonly int maxTries;

        private int tryCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="Backoff"/> class.
        /// </summary>
        /// <remarks>This defaults to 5 retries, with a back-off of 2 seconds. The minimum backoff is 1 second, and the maximum backoff is 30 seconds.</remarks>
        public Backoff()
            : this(5, TimeSpan.FromSeconds(2))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Backoff"/> class.
        /// </summary>
        /// <param name="maxTries">The maximum number of times the method will be tried.</param>
        /// <param name="deltaBackoff">The increase in the delay with each try.</param>
        /// <remarks>The  minimum backoff is 1 second, and the maximum backoff is 30 seconds.</remarks>
        public Backoff(int maxTries, TimeSpan deltaBackoff)
        {
            if (maxTries <= 0)
            {
                throw new ArgumentException("Max tries must be > 0", nameof(maxTries));
            }

            this.maxTries = maxTries;
            this.DeltaBackoff = deltaBackoff;
            this.MinBackoff = this.DefaultMinBackoff;
            this.MaxBackoff = this.DefaultMaxBackoff;
        }

        /// <inheritdoc/>
        public override bool CanRetry
        {
            get
            {
                return this.tryCount < this.maxTries;
            }
        }

        /// <summary>
        /// Gets the default minimum backoff time.
        /// </summary>
        /// <remarks>This is currently 1 second.</remarks>
        public TimeSpan DefaultMinBackoff
        {
            get { return TimeSpan.FromSeconds(1); }
        }

        /// <summary>
        /// Gets the default maximum backoff time.
        /// </summary>
        /// <remarks>This is currently 30 seconds.</remarks>
        public TimeSpan DefaultMaxBackoff
        {
            get { return TimeSpan.FromSeconds(30); }
        }

        /// <summary>
        /// Gets the amount by which the delay increases with each retry.
        /// </summary>
        public TimeSpan DeltaBackoff { get; }

        /// <summary>
        /// Gets or sets the minimum delay.
        /// </summary>
        public TimeSpan MinBackoff { get; set; }

        /// <summary>
        /// Gets or sets the maximum delay.
        /// </summary>
        public TimeSpan MaxBackoff { get; set; }

        /// <inheritdoc/>
        public override TimeSpan PrepareToRetry(Exception lastException)
        {
            if (lastException is null)
            {
                throw new ArgumentNullException(nameof(lastException));
            }

            this.AddException(lastException);

            this.tryCount += 1;

            if (this.CanRetry)
            {
                int increment = (int)((Math.Pow(2, this.tryCount) - 1) * NextRandom((int)(this.DeltaBackoff.TotalMilliseconds * 0.8), (int)(this.DeltaBackoff.TotalMilliseconds * 1.2)));
                int delay = (int)Math.Min(this.MinBackoff.TotalMilliseconds + increment, this.MaxBackoff.TotalMilliseconds);

                return TimeSpan.FromMilliseconds(delay);
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// Returns a random integer in the range [<paramref name="minValue"/>, <paramref name="maxValue"/>), from a
        /// generator shared by every <see cref="Backoff"/> rather than a new one for each retry.
        /// </summary>
        private static int NextRandom(int minValue, int maxValue)
        {
#if NET6_0_OR_GREATER
            return Random.Shared.Next(minValue, maxValue);
#else
            if (threadRandom is null)
            {
                int seed;
                lock (Seeds)
                {
                    seed = Seeds.Next();
                }

                threadRandom = new Random(seed);
            }

            return threadRandom.Next(minValue, maxValue);
#endif
        }
    }
}