// <copyright file="RetriableDelayTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Async;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// How <see cref="Retriable"/> waits between retries. The sleep service is process-wide, so these tests must not run in parallel.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class RetriableDelayTests
    {
        private RecordingSleepService sleeps = null!;

        [TestInitialize]
        public void Initialize() => this.sleeps = SleepServiceFixture.InstallRecording();

        [TestCleanup]
        public void Cleanup() => SleepServiceFixture.InstallForbidden();

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task WhenTheStrategyIsCount_NoSleepIsRequested(RetryShape shape)
        {
            var op = new FlakyOperation(2);

            await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Count(5), new AnyExceptionPolicy()).ConfigureAwait(false);

            Assert.AreEqual(3, op.Invocations);
            Assert.IsEmpty(this.sleeps.Records);
        }

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        public async Task SynchronousRetriesUseTheSynchronousSleep(RetryShape shape)
        {
            TimeSpan period = TimeSpan.FromMilliseconds(100);
            var op = new FlakyOperation(3);

            await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(period, 4), new AnyExceptionPolicy()).ConfigureAwait(false);

            Assert.AreEqual(4, op.Invocations);
            Assert.AreSequenceEqual(new[] { period, period, period }, this.sleeps.Delays);
            Assert.IsTrue(this.sleeps.Records.All(r => !r.IsAsync), "Synchronous retries must block with Sleep, not SleepAsync.");
        }

        [TestMethod]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task AsynchronousRetriesUseTheAsynchronousSleep(RetryShape shape)
        {
            TimeSpan period = TimeSpan.FromMilliseconds(100);
            var op = new FlakyOperation(3);

            await RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(period, 4), new AnyExceptionPolicy()).ConfigureAwait(false);

            Assert.AreEqual(4, op.Invocations);
            Assert.AreSequenceEqual(new[] { period, period, period }, this.sleeps.Delays);
            Assert.IsTrue(this.sleeps.Records.All(r => r.IsAsync), "Asynchronous retries must await SleepAsync, not block with Sleep.");
        }

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task TheOperationIsRetriedOnlyAfterTheSleep(RetryShape shape)
        {
            var log = new ConcurrentQueue<string>();
            this.sleeps.OnSleep = _ => log.Enqueue("sleep");
            var op = new FlakyOperation(2);

            await RetriableDriver.RetryAsync(
                shape,
                () =>
                {
                    log.Enqueue("try");
                    return op.Invoke();
                },
                CancellationToken.None,
                new Linear(TimeSpan.FromMilliseconds(100), 5),
                new AnyExceptionPolicy()).ConfigureAwait(false);

            Assert.AreSequenceEqual(new[] { "try", "sleep", "try", "sleep", "try" }, log.ToArray());
        }

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task IncrementalDelaysAreAppliedInSequence(RetryShape shape)
        {
            var op = new FlakyOperation(FlakyOperation.AlwaysFails);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Incremental(4, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50)), new AnyExceptionPolicy())).ConfigureAwait(false);

            Assert.AreEqual(4, op.Invocations);
            Assert.AreSequenceEqual(Milliseconds(100, 150, 200), this.sleeps.Delays);
        }

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task BackoffDelaysAreAppliedAndIncrease(RetryShape shape)
        {
            var op = new FlakyOperation(FlakyOperation.AlwaysFails);
            var strategy = new Backoff(4, TimeSpan.FromSeconds(1));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, strategy, new AnyExceptionPolicy())).ConfigureAwait(false);

            IReadOnlyList<TimeSpan> delays = this.sleeps.Delays;
            Assert.HasCount(3, delays);
            Assert.IsGreaterThanOrEqualTo(strategy.MinBackoff, delays[0]);
            Assert.IsGreaterThan(delays[0], delays[1]);
            Assert.IsGreaterThan(delays[1], delays[2]);
        }

        [TestMethod]
        [DataRow(RetryShape.Action)]
        [DataRow(RetryShape.Function)]
        [DataRow(RetryShape.AsyncAction)]
        [DataRow(RetryShape.AsyncFunction)]
        public async Task WhenTheRetryIsAbandoned_NoSleepIsRequestedForTheFinalFailure(RetryShape shape)
        {
            TimeSpan period = TimeSpan.FromMilliseconds(100);
            var op = new FlakyOperation(FlakyOperation.AlwaysFails);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => RetriableDriver.RetryAsync(shape, op.Invoke, CancellationToken.None, new Linear(period, 2), new AnyExceptionPolicy())).ConfigureAwait(false);

            Assert.AreEqual(2, op.Invocations);
            Assert.AreEqual(period, Assert.ContainsSingle(this.sleeps.Delays));
        }

        private static TimeSpan[] Milliseconds(params int[] milliseconds)
        {
            return Array.ConvertAll(milliseconds, ms => TimeSpan.FromMilliseconds(ms));
        }
    }
}
