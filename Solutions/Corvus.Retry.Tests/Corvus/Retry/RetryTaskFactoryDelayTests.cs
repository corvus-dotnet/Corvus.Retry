// <copyright file="RetryTaskFactoryDelayTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Async;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// <see cref="RetryTaskFactory"/> and <see cref="RetryTaskFactory{T}"/> wait for the strategy's delay before each retry.
    /// </summary>
    /// <remarks>
    /// The sleep service is process-wide, so these tests do not run in parallel with others.
    /// </remarks>
    [TestClass]
    [DoNotParallelize]
    public class RetryTaskFactoryDelayTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

        private RecordingSleepService sleeps = null!;

        [TestInitialize]
        public void Initialize() => this.sleeps = SleepServiceFixture.InstallRecording();

        [TestCleanup]
        public void Cleanup() => SleepServiceFixture.InstallForbidden();

        [TestMethod]
        public async Task RetriesWaitForTheStrategyDelay()
        {
            FlakyOperation operation = new(2);

            await new RetryTaskFactory().StartNew(() => operation.Invoke(), new Linear(Period, 3), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

            this.AssertSleeps(Period, Period);
            Assert.AreEqual(3, operation.Invocations);
        }

        [TestMethod]
        public async Task GenericRetriesWaitForTheStrategyDelay()
        {
            FlakyOperation operation = new(2);

            string result = await new RetryTaskFactory<string>().StartNew(operation.Invoke, new Linear(Period, 3), new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

            this.AssertSleeps(Period, Period);
            Assert.AreEqual("attempt-3", result);
        }

        [TestMethod]
        public async Task WhenCancellationIsRequestedDuringTheDelay_TheTaskIsCancelledWithoutWaitingForTheDelayToEnd()
        {
            // A sleep that never ends: the retry can only stop waiting because of cancellation.
            NeverEndingSleepService neverEnding = new();
            SleepService.SetSleepServiceForTesting(neverEnding);
            using CancellationTokenSource cancellation = new();
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), cancellation.Token, new Linear(TimeSpan.FromHours(1), 3), new AnyExceptionPolicy());

            await neverEnding.Entered.WaitAsync(Timeout).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);

            OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
            Assert.AreEqual(1, operation.Invocations);
        }

        [TestMethod]
        public async Task TheActionIsRetriedOnlyAfterTheSleep()
        {
            List<string> timeline = new();
            this.sleeps.OnSleep = _ =>
            {
                lock (timeline)
                {
                    timeline.Add("sleep");
                }
            };
            FlakyOperation operation = new(2);

            await new RetryTaskFactory().StartNew(
                () =>
                {
                    lock (timeline)
                    {
                        timeline.Add("try");
                    }

                    operation.Invoke();
                },
                new Linear(Period, 3),
                new AnyExceptionPolicy()).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreSequenceEqual(new[] { "try", "sleep", "try", "sleep", "try" }, timeline);
        }

        [TestMethod]
        public async Task WithCountStrategy_NoSleepIsRequested()
        {
            FlakyOperation operation = new(2);

            await new RetryTaskFactory().StartNew(() => operation.Invoke(), new Count(3)).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(3, operation.Invocations);
            Assert.IsEmpty(this.sleeps.Records);
        }

        [TestMethod]
        public async Task WhenTheRetryIsAbandoned_NoSleepIsRequestedForTheFinalFailure()
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = new RetryTaskFactory().StartNew(() => operation.Invoke(), new Linear(Period, 2), new AnyExceptionPolicy());

            await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(2, operation.Invocations);
            this.AssertSleeps(Period);
        }

        private void AssertSleeps(params TimeSpan[] expected)
        {
            // RetryTask blocks its thread-pool thread for the delay, but waits on SleepAsync so that cancellation
            // can end the wait early.
            Assert.AreSequenceEqual(expected, this.sleeps.Delays.ToArray());
        }

        private sealed class NeverEndingSleepService : ISleepService
        {
            private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => this.entered.Task;

            public void Sleep(TimeSpan timeSpan) => throw new NotSupportedException("RetryTask should wait on SleepAsync.");

            public Task SleepAsync(TimeSpan timeSpan)
            {
                this.entered.TrySetResult();
                return new TaskCompletionSource().Task;
            }
        }
    }
}
