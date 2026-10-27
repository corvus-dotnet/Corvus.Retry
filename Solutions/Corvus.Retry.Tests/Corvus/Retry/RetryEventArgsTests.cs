// <copyright file="RetryEventArgsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class RetryEventArgsTests
    {
        [TestMethod]
        public void ItExposesTheExceptionAndDelayItWasConstructedWith()
        {
            var exception = new InvalidOperationException();
            TimeSpan delay = TimeSpan.FromMilliseconds(1234);

            var args = new RetryEventArgs(exception, delay);

            Assert.AreSame(exception, args.LastException);
            Assert.AreEqual(delay, args.DelayBeforeRetry);
        }

        [TestMethod]
        public void WhenTheExceptionIsNull_TheConstructorThrows()
        {
            ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryEventArgs(null!, TimeSpan.Zero));

            Assert.AreEqual("lastException", ex.ParamName);
        }
    }
}
