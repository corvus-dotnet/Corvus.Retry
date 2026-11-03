// <copyright file="SleepServiceTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The default sleep service really waits. These are the only tests in the suite that sleep for real.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class SleepServiceTests
    {
        private static readonly TimeSpan Requested = TimeSpan.FromMilliseconds(25);

        // Timer resolution can make a wait finish fractionally early, so allow a little slack.
        private static readonly TimeSpan MinimumExpected = TimeSpan.FromMilliseconds(20);

        // A wait that the tests must never sit out: only cancellation can end it in time.
        private static readonly TimeSpan Long = TimeSpan.FromHours(1);

        private static readonly TimeSpan CancelAfter = TimeSpan.FromMilliseconds(25);

        // Far longer than a cancelled wait takes, but far shorter than Long. The point is "not an hour".
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

        [TestInitialize]
        public void Initialize() => SleepServiceFixture.InstallDefault();

        [TestCleanup]
        public void Cleanup() => SleepServiceFixture.InstallForbidden();

        [TestMethod]
        public void WhenTheTestServiceIsUnset_TheDefaultServiceSleepsForAtLeastTheRequestedTime()
        {
            var stopwatch = Stopwatch.StartNew();

            SleepService.Instance.Sleep(Requested, CancellationToken.None);

            Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
        }

        [TestMethod]
        public async Task WhenTheTestServiceIsUnset_TheDefaultServiceSleepsAsynchronouslyForAtLeastTheRequestedTime()
        {
            var stopwatch = Stopwatch.StartNew();

            await SleepService.Instance.SleepAsync(Requested, CancellationToken.None).ConfigureAwait(false);

            Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
        }

        [TestMethod]
        public void WhenTheTokenCanBeCancelledButIsNot_TheDefaultServiceSleepsForAtLeastTheRequestedTime()
        {
            using var cts = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();

            SleepService.Instance.Sleep(Requested, cts.Token);

            Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
        }

        [TestMethod]
        public async Task WhenTheTokenCanBeCancelledButIsNot_TheDefaultServiceSleepsAsynchronouslyForAtLeastTheRequestedTime()
        {
            using var cts = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();

            await SleepService.Instance.SleepAsync(Requested, cts.Token).ConfigureAwait(false);

            Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
        }

        [TestMethod]
        public void WhenCancellationIsRequestedDuringTheSleep_TheDefaultServiceStopsWaitingAndThrowsWithTheToken()
        {
            // The source's timer cancels the token from another thread while this thread is waiting.
            using var cts = new CancellationTokenSource(CancelAfter);
            var stopwatch = Stopwatch.StartNew();

            OperationCanceledException thrown = Assert.ThrowsExactly<OperationCanceledException>(
                () => SleepService.Instance.Sleep(Long, cts.Token));

            Assert.IsLessThan(Prompt, stopwatch.Elapsed);
            Assert.AreEqual(cts.Token, thrown.CancellationToken);
        }

        [TestMethod]
        public async Task WhenCancellationIsRequestedDuringTheAsynchronousSleep_TheDefaultServiceStopsWaitingAndThrowsWithTheToken()
        {
            using var cts = new CancellationTokenSource(CancelAfter);
            var stopwatch = Stopwatch.StartNew();

            // Task.Delay reports cancellation with TaskCanceledException, which derives from OperationCanceledException.
            TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                () => SleepService.Instance.SleepAsync(Long, cts.Token)).ConfigureAwait(false);

            Assert.IsLessThan(Prompt, stopwatch.Elapsed);
            Assert.AreEqual(cts.Token, thrown.CancellationToken);
        }

        [TestMethod]
        public void WhenTheTokenIsAlreadyCancelled_TheDefaultServiceThrowsWithoutSleeping()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var stopwatch = Stopwatch.StartNew();

            OperationCanceledException thrown = Assert.ThrowsExactly<OperationCanceledException>(
                () => SleepService.Instance.Sleep(Long, cts.Token));

            Assert.IsLessThan(Prompt, stopwatch.Elapsed);
            Assert.AreEqual(cts.Token, thrown.CancellationToken);
        }

        [TestMethod]
        public async Task WhenTheTokenIsAlreadyCancelled_TheDefaultServiceReturnsACancelledTaskWithoutSleeping()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync().ConfigureAwait(false);

            Task sleep = SleepService.Instance.SleepAsync(Long, cts.Token);

            Assert.IsTrue(sleep.IsCanceled, "The task should be cancelled at once, without waiting.");
            TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => sleep).ConfigureAwait(false);
            Assert.AreEqual(cts.Token, thrown.CancellationToken);
        }
    }
}
