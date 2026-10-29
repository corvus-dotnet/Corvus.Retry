// <copyright file="CountTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The <see cref="Count"/> strategy allows a fixed number of tries with no delay between them.
    /// </summary>
    [TestClass]
    public class CountTests
    {
        [TestMethod]
        public void WhenConstructedWithoutArguments_ItAllowsFiveTries()
        {
            var strategy = new Count();

            StrategyDriver.FailTimes(strategy, 4);
            Assert.IsTrue(strategy.CanRetry);

            StrategyDriver.FailTimes(strategy, 1);
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(3)]
        [DataRow(10)]
        public void WhenConstructedWithMaxTries_CanRetryBecomesFalseAfterThatManyFailures(int maxTries)
        {
            var strategy = new Count(maxTries);

            StrategyDriver.FailTimes(strategy, maxTries - 1);
            Assert.IsTrue(strategy.CanRetry);

            StrategyDriver.FailTimes(strategy, 1);
            Assert.IsFalse(strategy.CanRetry);
        }

        [TestMethod]
        public void BeforeAnyFailure_CanRetryIsTrue()
        {
            Assert.IsTrue(new Count(1).CanRetry);
        }

        [TestMethod]
        public void ItNeverDelaysBetweenRetries()
        {
            var strategy = new Count(3);

            IReadOnlyList<TimeSpan> delays = StrategyDriver.FailTimes(strategy, 3);

            Assert.AreSequenceEqual(new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }, delays);
        }

        [TestMethod]
        public void ItRecordsEveryFailureInOrder()
        {
            var strategy = new Count(3);
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
            ArgumentOutOfRangeException ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Count(maxTries));

            Assert.AreEqual("maxTries", ex.ParamName);
        }

        [TestMethod]
        public void WhenTheLastExceptionIsNull_PrepareToRetryThrows()
        {
            ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new Count().PrepareToRetry(null!));

            Assert.AreEqual("lastException", ex.ParamName);
        }
    }
}
