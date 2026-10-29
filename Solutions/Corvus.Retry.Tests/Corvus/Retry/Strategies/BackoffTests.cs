// <copyright file="BackoffTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The <see cref="Backoff"/> strategy increases the delay exponentially, with jitter, between bounds.
    /// </summary>
    /// <remarks>
    /// Before retry n (n = failures so far) the delay is
    /// <c>min(MinBackoff + (2^n - 1) * r, MaxBackoff)</c>, where r is a random whole number of milliseconds
    /// in <c>[0.8 * DeltaBackoff, 1.2 * DeltaBackoff)</c>. The bounds below are therefore deterministic.
    /// </remarks>
    [TestClass]
    public class BackoffTests
    {
        private const int Iterations = 20;

        [TestMethod]
        public void EachRetryDelayLiesWithinTheDocumentedJitterBand()
        {
            // With MinBackoff 1000ms and DeltaBackoff 1000ms, r lies in [800, 1200), giving
            // n=1: [1800, 2200], n=2: [3400, 4600], n=3: [6600, 9400] ms. Integer arithmetic keeps the bounds exact.
            const int MinMs = 1000;
            const int LowJitterMs = 800;
            const int HighJitterMs = 1200;

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                var strategy = new Backoff(4, TimeSpan.FromMilliseconds(1000));

                List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 3);

                for (int n = 1; n <= 3; n++)
                {
                    int factor = (1 << n) - 1;
                    TimeSpan lower = TimeSpan.FromMilliseconds(MinMs + (factor * LowJitterMs));
                    TimeSpan upper = TimeSpan.FromMilliseconds(MinMs + (factor * HighJitterMs));
                    Assert.IsInRange(lower, upper, delays[n - 1]);
                }
            }
        }

        [TestMethod]
        public void EachRetryWaitsLongerThanThePrevious()
        {
            // Deterministic despite the jitter: (2^n - 1) * 1.2 < (2^(n+1) - 1) * 0.8 for every n >= 1.
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                var strategy = new Backoff(5, TimeSpan.FromSeconds(1));

                List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 4);

                for (int i = 1; i < delays.Count; i++)
                {
                    Assert.IsGreaterThan(delays[i - 1], delays[i]);
                }
            }
        }

        [TestMethod]
        public void WhenTheDeltaIsZero_EveryRetryWaitsExactlyMinBackoff()
        {
            var strategy = new Backoff(4, TimeSpan.Zero);

            List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 3);

            Assert.AreSequenceEqual(new[] { strategy.MinBackoff, strategy.MinBackoff, strategy.MinBackoff }, delays);
            Assert.AreEqual(TimeSpan.FromSeconds(1), strategy.MinBackoff);
        }

        [TestMethod]
        public void TheDelayNeverExceedsMaxBackoff()
        {
            TimeSpan max = TimeSpan.FromSeconds(2);
            var strategy = new Backoff(5, TimeSpan.FromSeconds(1)) { MaxBackoff = max };

            List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 4);

            foreach (TimeSpan delay in delays)
            {
                Assert.IsLessThanOrEqualTo(max, delay);
            }

            // From the second retry onwards the uncapped delay is at least 1s + 3 * 0.8s, so the cap applies.
            Assert.AreSequenceEqual(new[] { max, max, max }, delays.GetRange(1, 3));
        }

        [TestMethod]
        public void RaisingMinBackoffRaisesEveryDelay()
        {
            TimeSpan min = TimeSpan.FromSeconds(5);
            var strategy = new Backoff(3, TimeSpan.Zero) { MinBackoff = min };

            List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 2);

            Assert.AreSequenceEqual(new[] { min, min }, delays);
        }

        [TestMethod]
        public void WhenConstructedWithoutArguments_ItAllowsFiveTriesWithATwoSecondDeltaAndDefaultBounds()
        {
            var strategy = new Backoff();

            Assert.AreEqual(TimeSpan.FromSeconds(2), strategy.DeltaBackoff);
            Assert.AreEqual(TimeSpan.FromSeconds(1), strategy.MinBackoff);
            Assert.AreEqual(TimeSpan.FromSeconds(30), strategy.MaxBackoff);
            Assert.AreEqual(strategy.DefaultMinBackoff, strategy.MinBackoff);
            Assert.AreEqual(strategy.DefaultMaxBackoff, strategy.MaxBackoff);

            StrategyDriver.FailTimes(strategy, 4);
            Assert.IsTrue(strategy.CanRetry);

            StrategyDriver.FailTimes(strategy, 1);
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        public void WhenNoRetriesRemain_TheDelayIsZero()
        {
            var strategy = new Backoff(2, TimeSpan.FromSeconds(1));
            strategy.PrepareToRetry(new InvalidOperationException());

            TimeSpan finalDelay = strategy.PrepareToRetry(new InvalidOperationException());

            Assert.AreEqual(TimeSpan.Zero, finalDelay);
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        public void ItRecordsEveryFailureInOrder()
        {
            var strategy = new Backoff(3, TimeSpan.FromSeconds(1));
            Exception[] failures = [new InvalidOperationException("1"), new TimeoutException("2"), new InvalidOperationException("3")];

            foreach (Exception failure in failures)
            {
                strategy.PrepareToRetry(failure);
            }

            IReadOnlyList<Exception> recorded = strategy.Exception.InnerExceptions;
            Assert.HasCount(3, recorded);
            for (int i = 0; i < failures.Length; i++)
            {
                Assert.AreSame(failures[i], recorded[i]);
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        public void WhenMaxTriesIsNotPositive_TheConstructorThrows(int maxTries)
        {
            ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Backoff(maxTries, TimeSpan.FromSeconds(1)));

            Assert.AreEqual("maxTries", ex.ParamName);
        }

        [TestMethod]
        public void WhenTheLastExceptionIsNull_PrepareToRetryThrows()
        {
            ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new Backoff().PrepareToRetry(null!));

            Assert.AreEqual("lastException", ex.ParamName);
        }
    }
}
