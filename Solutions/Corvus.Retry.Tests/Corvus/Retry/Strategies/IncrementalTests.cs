// <copyright file="IncrementalTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The <see cref="Incremental"/> strategy increases the delay by a fixed step before each retry.
    /// </summary>
    [TestClass]
    public class IncrementalTests
    {
        [TestMethod]
        public void ItIncreasesTheDelayByTheStepOnEachRetry()
        {
            // The README example: 5 tries, starting at 1 second, increasing by 1 second each time.
            var strategy = new Incremental(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 4);

            Assert.AreSequenceEqual(Seconds(1, 2, 3, 4), delays);
            Assert.IsTrue(strategy.CanRetry);
        }

        [TestMethod]
        public void WhenConstructedWithoutArguments_ItAllowsFiveTriesStartingAtOneSecondInTwoSecondSteps()
        {
            var strategy = new Incremental();

            List<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 4);
            Assert.AreSequenceEqual(Seconds(1, 3, 5, 7), delays);
            Assert.IsTrue(strategy.CanRetry);

            strategy.PrepareToRetry(new InvalidOperationException());
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        public void WhenNoRetriesRemain_TheDelayIsZero()
        {
            var strategy = new Incremental(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            strategy.PrepareToRetry(new InvalidOperationException());

            TimeSpan finalDelay = strategy.PrepareToRetry(new InvalidOperationException());

            Assert.AreEqual(TimeSpan.Zero, finalDelay);
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        public void ItRecordsEveryFailureInOrder()
        {
            var strategy = new Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
            ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(() => new Incremental(maxTries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

            Assert.AreEqual("maxTries", ex.ParamName);
        }

        [TestMethod]
        public void WhenTheLastExceptionIsNull_PrepareToRetryThrows()
        {
            ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new Incremental().PrepareToRetry(null!));

            Assert.AreEqual("lastException", ex.ParamName);
        }

        private static TimeSpan[] Seconds(params int[] seconds)
        {
            return Array.ConvertAll(seconds, s => TimeSpan.FromSeconds(s));
        }
    }
}
