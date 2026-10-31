// <copyright file="RetryStrategyTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The <see cref="RetryStrategy"/> base class is the documented extension point for custom strategies.
    /// </summary>
    [TestClass]
    public class RetryStrategyTests
    {
        [TestMethod]
        public void WhenAnAggregateExceptionIsRecorded_ItsInnerExceptionsAreRecordedIndividually()
        {
            var strategy = new WrappingStrategy(wrap: false);
            var first = new InvalidOperationException("first");
            var second = new TimeoutException("second");

            strategy.PrepareToRetry(new AggregateException(first, second));

            IReadOnlyList<Exception> recorded = strategy.Exception.InnerExceptions;
            Assert.HasCount(2, recorded);
            Assert.AreSame(first, recorded[0]);
            Assert.AreSame(second, recorded[1]);
        }

        [TestMethod]
        public void ADerivedStrategyCanRecordADecoratedException()
        {
            var strategy = new WrappingStrategy(wrap: true);
            var failure = new InvalidOperationException("failure");

            strategy.PrepareToRetry(failure);

            Exception recorded = Assert.ContainsSingle(strategy.Exception.InnerExceptions);
            Assert.IsInstanceOfType<WrappedFailureException>(recorded);
            Assert.AreSame(failure, recorded.InnerException);
        }

        [TestMethod]
        public void WhenRecordingANullException_AddExceptionThrows()
        {
            var strategy = new WrappingStrategy(wrap: false);

            ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => strategy.Record(null!));

            Assert.AreEqual("exception", ex.ParamName);
        }

        [TestMethod]
        public void OnRetryingRaisesRetryingWithTheStrategyAsSender()
        {
            var strategy = new WrappingStrategy(wrap: false);
            var args = new RetryEventArgs(new InvalidOperationException(), TimeSpan.FromSeconds(1));
            object? observedSender = null;
            RetryEventArgs? observedArgs = null;
            strategy.Retrying += (sender, e) =>
            {
                observedSender = sender;
                observedArgs = e;
            };

            strategy.OnRetrying(args);

            Assert.AreSame(strategy, observedSender);
            Assert.AreSame(args, observedArgs);
        }

        [TestMethod]
        public void OnRetryingWithNoSubscribersDoesNotThrow()
        {
            var strategy = new WrappingStrategy(wrap: false);

            strategy.OnRetrying(new RetryEventArgs(new InvalidOperationException(), TimeSpan.Zero));
        }

        private sealed class WrappingStrategy : RetryStrategy
        {
            private readonly bool wrap;

            public WrappingStrategy(bool wrap)
            {
                this.wrap = wrap;
            }

            public override bool CanRetry => true;

            public override TimeSpan PrepareToRetry(Exception lastException)
            {
                this.AddException(this.wrap ? new WrappedFailureException(lastException) : lastException);
                return TimeSpan.Zero;
            }

            public void Record(Exception exception)
            {
                this.AddException(exception);
            }
        }

        private sealed class WrappedFailureException : Exception
        {
            public WrappedFailureException(Exception inner)
                : base("Decorated failure", inner)
            {
            }
        }
    }
}
