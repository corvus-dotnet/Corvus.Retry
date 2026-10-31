// <copyright file="RetryTaskFactoryTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// <see cref="RetryTaskFactory"/> starts a task that runs an action and retries it according to a strategy and policy.
    /// </summary>
    /// <remarks>
    /// Every test uses a zero-delay strategy (<see cref="Count"/> or <see cref="DoNotRetry"/>), so none of them sleeps.
    /// </remarks>
    [TestClass]
    public class RetryTaskFactoryTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly object State = new();

        private readonly RetryTaskFactory factory = new();

        [TestMethod]
        public void TheOverloadDriverCoversEveryPublicStartNewOverload()
        {
            // Guards the [DynamicData] tests below: if an overload is added, the driver must learn about it.
            Assert.HasCount(CountPublicStartNewMethods(typeof(RetryTaskFactory)), RetryTaskFactoryDriver.AllOverloads);
            Assert.HasCount(CountPublicStartNewMethods(typeof(RetryTaskFactory<int>)), RetryTaskFactoryDriver.AllOverloads);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.All), typeof(RetryTaskFactoryDriver))]
        public async Task WhenTheActionFailsTwiceThenSucceeds_TheTaskCompletesAfterThreeInvocations(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(2);

            Task task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy());
            await task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.AreEqual(3, operation.Invocations);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithStrategy), typeof(RetryTaskFactoryDriver))]
        public async Task WhenEveryTryFails_TheTaskFaultsWithAnAggregateOfEveryFailure(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = this.StartNew(overload, defaults, operation, new Count(3), new AnyExceptionPolicy());

            // Unlike Retriable, which rethrows only the last failure, the task reports every failure in order.
            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(3, operation.Invocations);
            AssertSameExceptions(operation.Thrown, thrown.InnerExceptions);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithPolicy), typeof(RetryTaskFactoryDriver))]
        public async Task WhenThePolicyRejectsTheFailure_TheTaskFaultsAfterASingleInvocation(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);
            RecordingRetryPolicy policy = new(false);

            Task task = this.StartNew(overload, defaults, operation, new Count(5), policy);

            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(1, operation.Invocations);

            // The policy sees the operation's own exception, not the task's AggregateException.
            Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(policy.Consulted));
            Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
        }

        [TestMethod]
        public async Task WhenAnyInnerExceptionIsRejectedByThePolicy_TheTaskIsNotRetried()
        {
            TimeoutException acceptable = new();
            InvalidOperationException unacceptable = new();
            int invocations = 0;
            RecordingRetryPolicy policy = new(ex => ex is TimeoutException);

            Task task = this.factory.StartNew(
                () =>
                {
                    Interlocked.Increment(ref invocations);
                    throw new AggregateException(acceptable, unacceptable);
                },
                new Count(5),
                policy);

            await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(1, invocations);

            // An AggregateException thrown by the action is flattened, and the policy must accept every inner exception.
            Assert.HasCount(2, policy.Consulted);
            Assert.AreSame(acceptable, policy.Consulted[0]);
            Assert.AreSame(unacceptable, policy.Consulted[1]);
        }

        [TestMethod]
        public async Task WhenTheActionThrowsAnAggregateExceptionWithNoInnerExceptions_ThePolicyIsStillConsulted()
        {
            AggregateException failure = new("A failure with no inner exceptions");
            int invocations = 0;
            RecordingRetryPolicy policy = new(false);

            Task task = this.factory.StartNew(
                () =>
                {
                    Interlocked.Increment(ref invocations);
                    throw failure;
                },
                new Count(5),
                policy);

            await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);

            // There is nothing to flatten, so the policy is asked about the AggregateException itself, and says no.
            Assert.AreEqual(1, invocations);
            Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithState), typeof(RetryTaskFactoryDriver))]
        public async Task TheStateObjectIsPassedToEveryTry(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(2);
            List<object?> statesSeen = new();

            Task task = RetryTaskFactoryDriver.StartNew(
                this.factory,
                overload,
                defaults,
                state =>
                {
                    lock (statesSeen)
                    {
                        statesSeen.Add(state);
                    }

                    operation.Invoke();
                },
                State,
                CancellationToken.None,
                new Count(5),
                new AnyExceptionPolicy());
            await task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.HasCount(3, statesSeen);
            Assert.IsTrue(statesSeen.All(s => ReferenceEquals(s, State)), "Every try should receive the state object.");
        }

        [TestMethod]
        public async Task WhenTheActionSucceedsFirstTime_ItIsInvokedOnceAndThePolicyIsNotConsulted()
        {
            FlakyOperation operation = new(0);
            RecordingRetryPolicy policy = new(true);
            Count strategy = new(5);
            int retryingEvents = 0;
            strategy.Retrying += (_, _) => Interlocked.Increment(ref retryingEvents);

            await this.factory.StartNew(() => operation.Invoke(), strategy, policy).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(1, operation.Invocations);
            Assert.IsEmpty(policy.Consulted);
            Assert.AreEqual(0, retryingEvents);
        }

        [TestMethod]
        public async Task StartNewReturnsBeforeTheActionCompletes()
        {
            using ManualResetEventSlim gate = new();
            int completed = 0;

            Task task = this.factory.StartNew(() =>
            {
                gate.Wait(Timeout);
                Interlocked.Increment(ref completed);
            });

            Assert.IsFalse(task.IsCompleted);
            Assert.AreEqual(0, Volatile.Read(ref completed));

            gate.Set();
            await task.WaitAsync(Timeout).ConfigureAwait(false);
            Assert.AreEqual(1, completed);
        }

        [TestMethod]
        public async Task TheRetryingEventIsRaisedBeforeEachRetry()
        {
            FlakyOperation operation = new(2);
            Count strategy = new(4);
            List<(object? Sender, RetryEventArgs Args, int InvocationsSoFar)> events = new();
            strategy.Retrying += (sender, args) =>
            {
                lock (events)
                {
                    events.Add((sender, args, operation.Invocations));
                }
            };

            await this.factory.StartNew(() => operation.Invoke(), strategy).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.HasCount(2, events);
            for (int i = 0; i < events.Count; i++)
            {
                (object? sender, RetryEventArgs args, int invocationsSoFar) = events[i];
                Assert.AreSame(strategy, sender);
                Assert.AreEqual(i + 1, invocationsSoFar, "The event should be raised after a failure and before the next try.");
                Assert.AreEqual(TimeSpan.Zero, args.DelayBeforeRetry);

                // Unlike Retriable, which reports the raw exception, RetryTask reports the failed task's AggregateException.
                AggregateException lastException = Assert.IsInstanceOfType<AggregateException>(args.LastException);
                Assert.AreSame(operation.Thrown[i], Assert.ContainsSingle(lastException.InnerExceptions));
            }
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithoutStrategy), typeof(RetryTaskFactoryDriver))]
        public async Task WhenNoStrategyIsGiven_UpToFiveTriesAreMade(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = this.StartNew(overload, defaults, operation, strategy: null, new AnyExceptionPolicy());

            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(5, operation.Invocations);
            Assert.HasCount(5, thrown.InnerExceptions);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithStrategyOnly), typeof(RetryTaskFactoryDriver))]
        public async Task WhenOnlyAStrategyIsGiven_AnyExceptionIsRetried(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(
                3,
                attempt => attempt switch
                {
                    1 => new TimeoutException(),
                    2 => new ArgumentException("Bad argument"),
                    _ => new NotSupportedException(),
                });

            Task task = this.StartNew(overload, defaults, operation, new Count(5), policy: null);
            await task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(4, operation.Invocations);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
        public async Task WhenTheTokenIsAlreadyCancelled_TheActionNeverRunsAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
        {
            FlakyOperation operation = new(0);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync().ConfigureAwait(false);

            Task task = this.StartNew(overload, defaults, operation, new Count(5), new AnyExceptionPolicy(), cancellation.Token);

            TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
            Assert.AreEqual(0, operation.Invocations);
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
        public async Task WhenCancellationIsRequestedDuringRetries_TheActionIsNotRunAgainAndTheTaskIsCancelled(StartNewOverload overload, StartNewDefaults defaults)
        {
            using CancellationTokenSource cancellation = new();
            FlakyOperation operation = new(
                FlakyOperation.AlwaysFails,
                attempt =>
                {
                    cancellation.Cancel();
                    return new InvalidOperationException($"Attempt {attempt} failed");
                });
            Count strategy = new(5);
            int retryingRaised = 0;
            strategy.Retrying += (_, _) => Interlocked.Increment(ref retryingRaised);

            Task task = this.StartNew(overload, defaults, operation, strategy, new AnyExceptionPolicy(), cancellation.Token);

            // The operation would have been retried, but cancellation stopped it, so the task is cancelled with the
            // caller's token, and Retrying is not raised for a retry that will not run.
            TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
            Assert.AreEqual(1, operation.Invocations);
            Assert.AreEqual(0, Volatile.Read(ref retryingRaised));
        }

        [TestMethod]
        public async Task WithAttachedToParent_TheParentWaitsForTheOutcomeAndIsNotFaultedByTriesThatWereRetried()
        {
            FlakyOperation operation = new(2);
            Task? retryTask = null;

            Task parent = Task.Factory.StartNew(
                () => retryTask = this.factory.StartNew(() => operation.Invoke(), TaskCreationOptions.AttachedToParent, new Count(5), new AnyExceptionPolicy()),
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            await parent.WaitAsync(Timeout).ConfigureAwait(false);

            // The parent waited for the retried operation, and saw only its successful outcome.
            Assert.AreEqual(TaskStatus.RanToCompletion, parent.Status);
            Assert.IsNotNull(retryTask);
            Assert.AreEqual(TaskStatus.RanToCompletion, retryTask.Status);
            Assert.AreEqual(3, operation.Invocations);
        }

        [TestMethod]
        public async Task WithAttachedToParent_WhenEveryTryFails_TheParentFaultsWithTheOutcome()
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task parent = Task.Factory.StartNew(
                () => this.factory.StartNew(() => operation.Invoke(), TaskCreationOptions.AttachedToParent, new Count(3), new AnyExceptionPolicy()),
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => parent.WaitAsync(Timeout)).ConfigureAwait(false);

            // The parent reports the retried operation's failure once, not each try's.
            AggregateException outcome = Assert.IsInstanceOfType<AggregateException>(Assert.ContainsSingle(thrown.InnerExceptions));
            AssertSameExceptions(operation.Thrown, outcome.InnerExceptions);
        }

        [TestMethod]
        public async Task WithAScheduler_EveryTryRunsOnThatScheduler()
        {
            TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
            FlakyOperation operation = new(2);
            List<TaskScheduler> schedulers = new();

            Task task = this.factory.StartNew(
                () =>
                {
                    lock (schedulers)
                    {
                        schedulers.Add(TaskScheduler.Current);
                    }

                    operation.Invoke();
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler,
                new Count(5),
                new AnyExceptionPolicy());

            await task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.AreEqual(3, operation.Invocations);
            Assert.HasCount(3, schedulers);
            Assert.IsTrue(schedulers.All(s => s == scheduler), "Every try, not just the first, should run on the caller's scheduler.");
        }

        [TestMethod]
        public async Task WhenThePolicyThrows_TheTaskFaultsWithThePolicysException()
        {
            InvalidOperationException policyFailure = new("The policy failed");
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = this.factory.StartNew(() => operation.Invoke(), new Count(5), new RecordingRetryPolicy(_ => throw policyFailure));

            InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreSame(policyFailure, thrown);
            Assert.AreEqual(1, operation.Invocations);
        }

        [TestMethod]
        public async Task WhenCancellationIsRequestedButTheFailureWouldNotBeRetried_TheTaskFaultsWithTheFailure()
        {
            using CancellationTokenSource cancellation = new();
            FlakyOperation operation = new(
                FlakyOperation.AlwaysFails,
                attempt =>
                {
                    cancellation.Cancel();
                    return new InvalidOperationException($"Attempt {attempt} failed");
                });

            Task task = this.factory.StartNew(() => operation.Invoke(), cancellation.Token, new Count(5), new RecordingRetryPolicy(false));

            // Cancellation did not stop anything: the policy had already ruled out a retry. So the failure is reported.
            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
        }

        [TestMethod]
        [DynamicData(nameof(RetryTaskFactoryDriver.WithCancellationToken), typeof(RetryTaskFactoryDriver))]
        public async Task WhenARetryAcknowledgesCancellation_AwaitingTheTaskThrowsTaskCanceledException(StartNewOverload overload, StartNewDefaults defaults)
        {
            using CancellationTokenSource cancellation = new();
            int invocations = 0;

            Task task = RetryTaskFactoryDriver.StartNew(
                this.factory,
                overload,
                defaults,
                _ =>
                {
                    if (Interlocked.Increment(ref invocations) == 1)
                    {
                        throw new InvalidOperationException("Transient failure");
                    }

                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                },
                State,
                cancellation.Token,
                new Count(5),
                new AnyExceptionPolicy());

            TaskCanceledException thrown = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
            Assert.AreEqual(2, invocations);
        }

        [TestMethod]
        public async Task WhenTheStrategyIsDoNotRetry_TheTaskFaultsWithTheOriginalException()
        {
            FlakyOperation operation = new(FlakyOperation.AlwaysFails);

            Task task = this.factory.StartNew(() => operation.Invoke(), new DoNotRetry(), new AnyExceptionPolicy());

            // DoNotRetry records no exceptions, so the task reports the action's own failure.
            AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.WaitAsync(Timeout)).ConfigureAwait(false);
            Assert.AreEqual(1, operation.Invocations);
            Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(thrown.InnerExceptions));
        }

        private static int CountPublicStartNewMethods(Type factoryType)
        {
            return factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Count(m => m.Name == "StartNew");
        }

        private static void AssertSameExceptions(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
        {
            Assert.HasCount(expected.Count, actual);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.AreSame(expected[i], actual[i]);
            }
        }

        private Task StartNew(
            StartNewOverload overload,
            StartNewDefaults defaults,
            FlakyOperation operation,
            IRetryStrategy? strategy,
            IRetryPolicy? policy,
            CancellationToken cancellationToken = default)
        {
            return RetryTaskFactoryDriver.StartNew(this.factory, overload, defaults, _ => operation.Invoke(), State, cancellationToken, strategy, policy);
        }
    }
}
