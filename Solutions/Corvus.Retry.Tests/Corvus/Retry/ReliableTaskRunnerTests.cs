// <copyright file="ReliableTaskRunnerTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Pins the behaviour of <see cref="ReliableTaskRunner"/>: a host that keeps a long-running operation going,
/// restarting it after a failure for as long as the retry policy allows, until <see cref="ReliableTaskRunner.StopAsync"/>
/// or <see cref="ReliableTaskRunner.DisposeAsync"/>.
/// </summary>
/// <remarks>
/// These tests never sleep. They synchronise with <see cref="TaskCompletionSource"/> gates and the runner's
/// cancellation token, and every wait is bounded by <see cref="Timeout"/> so that a regression fails instead of hanging.
/// </remarks>
[TestClass]
public class ReliableTaskRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public async Task RunStartsTheOperationImmediatelyWithATokenThatIsNotYetCancelled()
    {
        int invocations = 0;
        CancellationToken token = default;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
        {
            Interlocked.Increment(ref invocations);
            token = ct;
            return WaitForCancellationAsync(ct);
        });

        // Run invokes the operation before it returns.
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsTrue(token.CanBeCanceled);
        Assert.IsFalse(token.IsCancellationRequested);

        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WhenStopAsyncIsCalled_TheTokenIsCancelledAndTheReturnedTaskCompletesWhenTheOperationReturns()
    {
        TaskCompletionSource cancellationObserved = NewSignal();
        TaskCompletionSource allowOperationToReturn = NewSignal();

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            cancellationObserved.TrySetResult();
            await allowOperationToReturn.Task.ConfigureAwait(false);
        });

        Task stopTask = runner.StopAsync();
        await WaitAsync(cancellationObserved.Task, "the operation to observe cancellation").ConfigureAwait(false);

        // The operation has seen the cancellation but has not returned yet, so stopping is still in progress.
        Assert.IsFalse(stopTask.IsCompleted);

        allowOperationToReturn.TrySetResult();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
    }

    [TestMethod]
    public async Task WhenTheOperationFailsAndThePolicyAllowsIt_TheOperationIsRestarted()
    {
        var failure = new InvalidOperationException("First run failed");
        var policy = new RecordingRetryPolicy(true);
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(failure);
                }

                secondRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            policy);

        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));

        // The policy sees the exception the operation threw, not an AggregateException wrapping it (F5).
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
    }

    [TestMethod]
    public async Task WhenThePolicyAcceptsOnlyTheSpecificExceptionTypeTheOperationThrows_TheOperationIsRestarted()
    {
        // The README-style policy: "retry only for this exception type". Before F5 the policy was handed an
        // AggregateException, so a type test like this one never matched and the runner never restarted.
        var policy = new RecordingRetryPolicy(exception => exception is TimeoutException);
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(new TimeoutException("Transient failure"));
                }

                secondRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            policy);

        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        Assert.IsInstanceOfType<TimeoutException>(Assert.ContainsSingle(policy.Consulted));

        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ItKeepsRestartingForAsLongAsThePolicyAllows()
    {
        const int RunsAllowed = 4;
        var operation = new FlakyOperation(FlakyOperation.AlwaysFails);
        TaskCompletionSource policyRefused = NewSignal();
        int consultations = 0;
        var policy = new RecordingRetryPolicy(_ =>
        {
            if (Interlocked.Increment(ref consultations) < RunsAllowed)
            {
                return true;
            }

            policyRefused.TrySetResult();
            return false;
        });

        ReliableTaskRunner runner = ReliableTaskRunner.Run(_ => FailAsynchronously(operation.Invoke), policy);

        await WaitAsync(policyRefused.Task, "the policy to refuse a restart").ConfigureAwait(false);

        Assert.AreEqual(RunsAllowed, operation.Invocations);
        IReadOnlyList<Exception> thrown = operation.Thrown;
        IReadOnlyList<Exception> consulted = policy.Consulted;
        Assert.HasCount(RunsAllowed, consulted);
        for (int i = 0; i < RunsAllowed; i++)
        {
            Assert.AreSame(thrown[i], consulted[i]);
        }

        // The runner's task is now the final, faulted run.
        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(thrown[RunsAllowed - 1], actual);
    }

    [TestMethod]
    public async Task WhenARestartedRunFailsAndIsRestartedAgain_ItsFailureIsNotReportedAsUnobserved()
    {
        var failures = new[] { new InvalidOperationException("Run 1 failed"), new InvalidOperationException("Run 2 failed") };
        TaskCompletionSource thirdRunStarted = NewSignal();
        int invocations = 0;
        var unobserved = new List<Exception>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                // Tests run in parallel, so only count the failures this test caused.
                unobserved.AddRange(e.Exception.InnerExceptions.Where(ex => Array.IndexOf(failures, ex) >= 0));
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
            {
                int run = Interlocked.Increment(ref invocations);
                if (run <= failures.Length)
                {
                    return FailAsynchronously(failures[run - 1]);
                }

                thirdRunStarted.TrySetResult();
                return WaitForCancellationAsync(ct);
            });

            await WaitAsync(thirdRunStarted.Task, "the third run to start").ConfigureAwait(false);

            // The second run's task is no longer the runner's, so a fault nobody observed is reported when it
            // is finalized.
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.IsEmpty(unobserved);
        }
    }

    [TestMethod]
    public async Task WhenTheOperationFailsAndThePolicySaysNo_ItIsNotRestartedAndStopAsyncReturnsTheFaultedTask()
    {
        var failure = new InvalidOperationException("Permanent failure");
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(failure);
            },
            policy);

        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.IsTrue(stopTask.IsFaulted);
        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task WhenTheLastRunFaultedWithSeveralExceptions_StopAsyncReturnsATaskWithAllOfThem()
    {
        var first = new InvalidOperationException("First failure");
        var second = new FormatException("Second failure");
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ => Task.WhenAll(FailAsynchronously(first), FailAsynchronously(second)),
            policy);

        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.IsTrue(stopTask.IsFaulted);
        IReadOnlyCollection<Exception> thrown = stopTask.Exception!.InnerExceptions;
        Assert.HasCount(2, thrown);
        Assert.Contains(first, thrown);
        Assert.Contains(second, thrown);
    }

    [TestMethod]
    public async Task WhenATokenCallbackThrows_StopAsyncReturnsAFaultedTask()
    {
        var callbackFailure = new InvalidOperationException("Callback failure");

        ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
        {
            ct.Register(() => throw callbackFailure);
            return WaitForCancellationAsync(ct);
        });

        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));

        // The task holds the callback's own exception, not the AggregateException in which CancelAsync wraps it.
        Assert.AreSame(callbackFailure, Assert.ContainsSingle(stopTask.Exception!.InnerExceptions));
    }

    [TestMethod]
    public async Task WhenATokenCallbackThrows_StopAsyncStillWaitsForTheRunToEnd()
    {
        var callbackFailure = new FormatException("Callback failure");
        var runFailure = new InvalidOperationException("The run failed while stopping");
        TaskCompletionSource callbackEntered = NewSignal();
        TaskCompletionSource allowRunToEnd = NewSignal();

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            ct.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFailure;
            });

            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            await allowRunToEnd.Task.ConfigureAwait(false);
            throw runFailure;
        });

        Task stopTask = runner.StopAsync();
        await WaitAsync(callbackEntered.Task, "the token callback to run").ConfigureAwait(false);

        // The callback has thrown, and the run is still going, so the runner has not stopped.
        await AssertStaysIncompleteAsync(stopTask, "StopAsync while the run is still going").ConfigureAwait(false);

        allowRunToEnd.TrySetResult();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));

        // The run's failure is in the task, so the task waited for the run to end.
        Assert.AreSequenceEqual(new Exception[] { callbackFailure, runFailure }, stopTask.Exception!.InnerExceptions.ToArray());
    }

    [TestMethod]
    [DataRow(false, DisplayName = "The run returns")]
    [DataRow(true, DisplayName = "The run honours cancellation by throwing")]
    public async Task WhenATokenCallbackThrowsAndTheRunDoesNotFail_StopAsyncWaitsForTheRunAndFaultsWithOnlyTheCallbackException(bool runThrowsCancellation)
    {
        var callbackFailure = new FormatException("Callback failure");
        TaskCompletionSource callbackEntered = NewSignal();
        TaskCompletionSource allowRunToEnd = NewSignal();
        int runsEnded = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            ct.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFailure;
            });

            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            await allowRunToEnd.Task.ConfigureAwait(false);
            Interlocked.Increment(ref runsEnded);
            if (runThrowsCancellation)
            {
                ct.ThrowIfCancellationRequested();
            }
        });

        Task stopTask = runner.StopAsync();

        // Record, at the moment the task completes, whether the run had ended.
        Task<bool> runHadEndedWhenStopped = stopTask.ContinueWith(
            _ => Volatile.Read(ref runsEnded) == 1,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await WaitAsync(callbackEntered.Task, "the token callback to run").ConfigureAwait(false);
        await AssertStaysIncompleteAsync(stopTask, "StopAsync while the run is still going").ConfigureAwait(false);

        allowRunToEnd.TrySetResult();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));

        Assert.IsTrue(await runHadEndedWhenStopped.ConfigureAwait(false));
        Assert.AreSame(callbackFailure, Assert.ContainsSingle(stopTask.Exception!.InnerExceptions));
    }

    [TestMethod]
    public async Task WhenSeveralTokenCallbacksThrow_StopAsyncFaultsWithEachOfThemFollowedByTheRunsFailure()
    {
        var callbackFailures = new Exception[] { new FormatException("Callback 1 failed"), new FormatException("Callback 2 failed") };
        var runFailure = new InvalidOperationException("The run failed while stopping");

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            foreach (Exception callbackFailure in callbackFailures)
            {
                ct.Register(() => throw callbackFailure);
            }

            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            throw runFailure;
        });

        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));

        // The token runs its callbacks in an order that it does not document, so check only that the callback
        // exceptions come first.
        Exception[] thrown = stopTask.Exception!.InnerExceptions.ToArray();
        Assert.HasCount(3, thrown);
        Assert.Contains(callbackFailures[0], thrown[..2]);
        Assert.Contains(callbackFailures[1], thrown[..2]);
        Assert.AreSame(runFailure, thrown[2]);
    }

    [TestMethod]
    public async Task WhenATokenCallbackThrows_DisposeAsyncCompletesSuccessfully()
    {
        var callbackFailure = new FormatException("Callback failure");
        var runFailure = new InvalidOperationException("The run failed while stopping");
        TaskCompletionSource callbackEntered = NewSignal();
        TaskCompletionSource allowRunToEnd = NewSignal();

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            ct.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFailure;
            });

            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            await allowRunToEnd.Task.ConfigureAwait(false);
            throw runFailure;
        });

        Task disposal = runner.DisposeAsync().AsTask();
        await WaitAsync(callbackEntered.Task, "the token callback to run").ConfigureAwait(false);
        await AssertStaysIncompleteAsync(disposal, "DisposeAsync while the run is still going").ConfigureAwait(false);

        allowRunToEnd.TrySetResult();
        Assert.AreEqual(TaskStatus.RanToCompletion, await WaitAsync(disposal, "the runner to be disposed").ConfigureAwait(false));

        // StopAsync after DisposeAsync cancels nothing, so it reports only how the last run ended.
        Task stopTask = runner.StopAsync();
        Assert.AreEqual(TaskStatus.Faulted, await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false));
        Assert.AreSame(runFailure, Assert.ContainsSingle(stopTask.Exception!.InnerExceptions));
    }

    [TestMethod]
    public async Task WhenTheOperationFailsWithSeveralExceptions_ItIsRestartedOnlyIfThePolicyAcceptsEveryOne()
    {
        var accepted = new TimeoutException("Transient failure");
        var rejected = new InvalidOperationException("Permanent failure");
        TaskCompletionSource rejectionSeen = NewSignal();
        var policy = new RecordingRetryPolicy(exception =>
        {
            if (exception is TimeoutException)
            {
                return true;
            }

            rejectionSeen.TrySetResult();
            return false;
        });
        int invocations = 0;

        // A task can fault with several exceptions (e.g. Task.WhenAll). Task.WhenAll does not guarantee the order
        // of its inner exceptions, so fault the task explicitly to make the order, and so the consultations, fixed.
        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                TaskCompletionSource failed = NewSignal();
                _ = Task.Run(() => failed.TrySetException(new Exception[] { accepted, rejected }));
                return failed.Task;
            },
            policy);

        await WaitAsync(rejectionSeen.Task, "the policy to reject a failure").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.IsTrue(stopTask.IsFaulted);
        Assert.AreEqual(1, Volatile.Read(ref invocations));

        // The policy is asked about each individual failure in turn.
        IReadOnlyList<Exception> consulted = policy.Consulted;
        Assert.HasCount(2, consulted);
        Assert.AreSame(accepted, consulted[0]);
        Assert.AreSame(rejected, consulted[1]);
    }

    [TestMethod]
    public async Task WhenTheOperationFailsWithAnAggregateExceptionWithNoInnerExceptions_ThePolicyIsStillConsulted()
    {
        var failure = new AggregateException("A failure with no inner exceptions");
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(failure);
            },
            policy);

        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        // There is nothing to flatten, so the policy is asked about the AggregateException itself, and says no.
        Assert.IsTrue(stopTask.IsFaulted);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.AreSame(failure, Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task WhenTheOperationCancelsItselfAndThePolicyAllowsIt_TheOperationIsRestarted()
    {
        // Cancellation the runner did not ask for, such as an HttpClient timeout, is a failure like any other.
        using CancellationTokenSource timeout = new();
        await timeout.CancelAsync().ConfigureAwait(false);
        var policy = new RecordingRetryPolicy(true);
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    await Task.Yield();
                    timeout.Token.ThrowIfCancellationRequested();
                }

                secondRunStarted.TrySetResult();
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
            },
            policy);

        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        OperationCanceledException consulted = Assert.IsInstanceOfType<OperationCanceledException>(Assert.ContainsSingle(policy.Consulted));
        Assert.AreEqual(timeout.Token, consulted.CancellationToken);

        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WhenTheOperationCancelsItselfAndThePolicySaysNo_ItIsNotRestartedAndStopAsyncReturnsTheCancelledTask()
    {
        using CancellationTokenSource timeout = new();
        await timeout.CancelAsync().ConfigureAwait(false);
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async _ =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Yield();
                timeout.Token.ThrowIfCancellationRequested();
            },
            policy);

        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.IsTrue(stopTask.IsCanceled);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsInstanceOfType<OperationCanceledException>(Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task WhenNoPolicyIsGiven_AnyFailureCausesARestart()
    {
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                return FailAsynchronously(new FormatException("An unusual failure"));
            }

            secondRunStarted.TrySetResult();
            return WaitForCancellationAsync(ct);
        });

        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WhenTheOperationCompletesWithoutFailing_ItIsNotRestarted()
    {
        var policy = new RecordingRetryPolicy(true);
        int invocations = 0;

        // The operation has already completed when Run attaches its failure continuation, so the runner has
        // made its restart decision by the time Run returns.
        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return Task.CompletedTask;
            },
            policy);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task AfterStopAsync_AFailingOperationIsNotRestarted()
    {
        var failure = new InvalidOperationException("Failed while stopping");
        var policy = new RecordingRetryPolicy(true);
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async ct =>
            {
                Interlocked.Increment(ref invocations);
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
                throw failure;
            },
            policy);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        // The failure is reported to the caller of StopAsync rather than causing a restart.
        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenStopAsyncIsCalledWhileTheRestartedOperationIsStarting_ItReturnsTheRestartedRun()
    {
        var failure = new InvalidOperationException("First run failed");
        TaskCompletionSource restartEntered = NewSignal();
        using var allowRestartToReturn = new ManualResetEventSlim();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(failure);
                }

                // Hold the restart inside runFunction, before it has returned its task.
                restartEntered.TrySetResult();
                allowRestartToReturn.Wait(Timeout);
                return WaitForCancellationAsync(ct);
            },
            new RecordingRetryPolicy(true));

        await WaitAsync(restartEntered.Task, "the restart to begin").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();

        // StopAsync must not hand back the faulted first run while a second one is starting.
        Assert.IsFalse(stopTask.IsCompleted);

        allowRestartToReturn.Set();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
        Assert.AreEqual(2, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenStopAsyncIsCalledWhileThePolicyIsDeciding_TheOperationIsNotRestarted()
    {
        var failure = new InvalidOperationException("Failed just before stopping");
        TaskCompletionSource policyEntered = NewSignal();
        using var allowPolicyToReturn = new ManualResetEventSlim();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyEntered.TrySetResult();
            allowPolicyToReturn.Wait(Timeout);
            return true;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(failure);
            },
            policy);

        await WaitAsync(policyEntered.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();

        // StopAsync waits for the restart decision, so that the task it returns represents the last run.
        Assert.IsFalse(stopTask.IsCompleted);

        allowPolicyToReturn.Set();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenTheOperationHonoursCancellationByThrowing_StopAsyncReturnsACancelledTask()
    {
        var policy = new RecordingRetryPolicy(true);

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async ct =>
            {
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            },
            policy);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        Assert.IsTrue(stopTask.IsCanceled);
        await Assert.ThrowsAsync<OperationCanceledException>(() => stopTask).ConfigureAwait(false);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task StopAsyncUsesCancelAsync()
    {
        // True only on this thread, and only while StopAsync runs, so the callback can tell if StopAsync ran it inline.
        using var insideStopAsync = new ThreadLocal<bool>();
        TaskCompletionSource<bool> callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowCallbackToReturn = new ManualResetEventSlim();

        ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
        {
            ct.Register(() =>
            {
                callbackEntered.TrySetResult(insideStopAsync.Value);
                allowCallbackToReturn.Wait(Timeout);
            });

            return WaitForCancellationAsync(ct);
        });

        insideStopAsync.Value = true;
        Task stopTask = runner.StopAsync();
        insideStopAsync.Value = false;

        // StopAsync returned while the callback is still blocked, so the callback did not run inline.
        await WaitAsync(callbackEntered.Task, "the token callback to run").ConfigureAwait(false);
        Assert.IsFalse(await callbackEntered.Task.ConfigureAwait(false));
        Assert.IsFalse(stopTask.IsCompleted);

        allowCallbackToReturn.Set();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
    }

    [TestMethod]
    public async Task DisposeAsyncStopsTheRunnerAndDoesNotThrowTheRunsException()
    {
        var failure = new InvalidOperationException("Failed while stopping");
        var policy = new RecordingRetryPolicy(true);
        TaskCompletionSource runEnded = NewSignal();
        CancellationToken token = default;
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async ct =>
            {
                Interlocked.Increment(ref invocations);
                token = ct;
                try
                {
                    await WaitForCancellationAsync(ct).ConfigureAwait(false);
                    throw failure;
                }
                finally
                {
                    runEnded.TrySetResult();
                }
            },
            policy);

        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);

        // DisposeAsync waited for the run to end, and swallowed its failure instead of restarting it.
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsTrue(runEnded.Task.IsCompleted);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task DisposeAsyncTwiceIsSafe()
    {
        int invocations = 0;
        ReliableTaskRunner runner = ReliableTaskRunner.Run(ct =>
        {
            Interlocked.Increment(ref invocations);
            return WaitForCancellationAsync(ct);
        });

        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);

        // The second call does nothing, so it completes at once.
        ValueTask secondDisposal = runner.DisposeAsync();
        Assert.IsTrue(secondDisposal.IsCompletedSuccessfully);
        await secondDisposal.ConfigureAwait(false);

        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task StopAsyncAfterDisposeAsyncReturnsTheLastRun()
    {
        var failure = new InvalidOperationException("Permanent failure");
        TaskCompletionSource policyConsulted = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyConsulted.TrySetResult();
            return false;
        });

        ReliableTaskRunner runner = ReliableTaskRunner.Run(_ => FailAsynchronously(failure), policy);
        await WaitAsync(policyConsulted.Task, "the policy to be consulted").ConfigureAwait(false);

        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);

        // StopAsync does not cancel the disposed source, and still reports how the last run ended.
        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    public async Task DisposeAsyncAfterStopAsyncDoesNotThrowTheRunsException()
    {
        var failure = new InvalidOperationException("Failed while stopping");

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            throw failure;
        });

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));

        // StopAsync has reported the failure; DisposeAsync only releases the runner.
        await WaitAsync(runner.DisposeAsync().AsTask(), "the runner to be disposed").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WhenDisposeAsyncIsCalledWhileTheRestartedOperationIsStarting_ItWaitsForTheRestartedRun()
    {
        var failure = new InvalidOperationException("First run failed");
        TaskCompletionSource restartEntered = NewSignal();
        using var allowRestartToReturn = new ManualResetEventSlim();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    return FailAsynchronously(failure);
                }

                // Hold the restart inside runFunction, before it has returned its task.
                restartEntered.TrySetResult();
                allowRestartToReturn.Wait(Timeout);
                return WaitForCancellationAsync(ct);
            },
            new RecordingRetryPolicy(true));

        await WaitAsync(restartEntered.Task, "the restart to begin").ConfigureAwait(false);

        Task disposal = runner.DisposeAsync().AsTask();

        // The source must not be disposed while the restarted run can still use its token.
        Assert.IsFalse(disposal.IsCompleted);

        allowRestartToReturn.Set();
        await WaitAsync(disposal, "the runner to be disposed").ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, disposal.Status);

        // The restarted run is the last run, and it ended when its token was cancelled.
        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.RanToCompletion, stopTask.Status);
        Assert.AreEqual(2, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task AwaitUsingStopsTheRunnerWhenTheBlockEnds()
    {
        TaskCompletionSource runStarted = NewSignal();
        TaskCompletionSource runEnded = NewSignal();

        ReliableTaskRunner runner = ReliableTaskRunner.Run(async ct =>
        {
            runStarted.TrySetResult();
            await WaitForCancellationAsync(ct).ConfigureAwait(false);
            runEnded.TrySetResult();
        });

        await using (runner.ConfigureAwait(false))
        {
            await WaitAsync(runStarted.Task, "the operation to start").ConfigureAwait(false);
            Assert.IsFalse(runEnded.Task.IsCompleted);
        }

        Assert.IsTrue(runEnded.Task.IsCompleted);
    }

    [TestMethod]
    public void WhenTheOperationThrowsSynchronously_RunPropagatesTheException()
    {
        var failure = new InvalidOperationException("Failed before returning a task");
        var policy = new RecordingRetryPolicy(true);

        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => ReliableTaskRunner.Run(_ => throw failure, policy));

        Assert.AreSame(failure, actual);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public void WhenTheFunctionIsNull_RunThrows()
    {
        ArgumentNullException withoutPolicy = Assert.ThrowsExactly<ArgumentNullException>(
            () => ReliableTaskRunner.Run(null!));
        ArgumentNullException withPolicy = Assert.ThrowsExactly<ArgumentNullException>(
            () => ReliableTaskRunner.Run(null!, new AnyExceptionPolicy()));

        Assert.AreEqual("runFunction", withoutPolicy.ParamName);
        Assert.AreEqual("runFunction", withPolicy.ParamName);
    }

    [TestMethod]
    public void WhenThePolicyIsNull_RunThrowsWithoutStartingTheOperation()
    {
        int invocations = 0;

        ArgumentNullException actual = Assert.ThrowsExactly<ArgumentNullException>(
            () => ReliableTaskRunner.Run(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return Task.CompletedTask;
                },
                null!));

        Assert.AreEqual("retryPolicy", actual.ParamName);
        Assert.AreEqual(0, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public void WhenTheRestartStrategyIsNull_RunThrowsArgumentNullException()
    {
        int invocations = 0;

        ArgumentNullException actual = Assert.ThrowsExactly<ArgumentNullException>(
            () => ReliableTaskRunner.Run(
                _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return Task.CompletedTask;
                },
                null!,
                AnyExceptionPolicy.Default));

        Assert.AreEqual("restartStrategy", actual.ParamName);
        Assert.AreEqual(0, Volatile.Read(ref invocations));
        Assert.AreEqual(
            "runFunction",
            Assert.ThrowsExactly<ArgumentNullException>(() => ReliableTaskRunner.Run(null!, new Count(1), AnyExceptionPolicy.Default)).ParamName);
        Assert.AreEqual(
            "retryPolicy",
            Assert.ThrowsExactly<ArgumentNullException>(() => ReliableTaskRunner.Run(_ => Task.CompletedTask, new Count(1), null!)).ParamName);
    }

    [TestMethod]
    public async Task WhenTheRestartStrategyRefuses_TheRunnerDoesNotRestartAndStopAsyncFaultsWithTheLastFailure()
    {
        var operation = new FlakyOperation(FlakyOperation.AlwaysFails);
        var strategy = new RecordingRestartStrategy(new Count(2));
        var policy = new RecordingRetryPolicy(true);

        ReliableTaskRunner runner = ReliableTaskRunner.Run(_ => FailAsynchronously(operation.Invoke), strategy, policy);

        await WaitAsync(strategy.Refused, "the strategy to refuse a restart").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        // Count(2) allows 2 runs. The strategy refused the third, so the policy saw only the first failure.
        Assert.AreEqual(2, operation.Invocations);
        Assert.AreSame(operation.Thrown[0], Assert.ContainsSingle(policy.Consulted));
        InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(operation.Thrown[1], actual);
    }

    [TestMethod]
    public async Task TheRestartStrategyReceivesTheNumberOfFailedRunsAndTheExceptionOfTheRun()
    {
        var failure = new InvalidOperationException("Run 1 failed");
        using CancellationTokenSource timeout = new();
        await timeout.CancelAsync().ConfigureAwait(false);
        var strategy = new RecordingRestartStrategy(RetryStrategies.FromDelegate(_ => TimeSpan.Zero));
        TaskCompletionSource thirdRunStarted = NewSignal();
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            async ct =>
            {
                switch (Interlocked.Increment(ref invocations))
                {
                    case 1:
                        await Task.Yield();
                        throw failure;
                    case 2:
                        // A cancellation that the runner did not ask for, such as an HttpClient timeout.
                        await Task.Yield();
                        timeout.Token.ThrowIfCancellationRequested();
                        break;
                }

                thirdRunStarted.TrySetResult();
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
            },
            strategy,
            AnyExceptionPolicy.Default);

        await WaitAsync(thirdRunStarted.Task, "the third run to start").ConfigureAwait(false);
        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);

        IReadOnlyList<RetryAttempt> attempts = strategy.Attempts;
        Assert.HasCount(2, attempts);

        // The strategy gets the AggregateException of the failed run, as RetryTask passes it.
        Assert.AreEqual(1, attempts[0].AttemptNumber);
        AggregateException aggregate = Assert.IsInstanceOfType<AggregateException>(attempts[0].Exception);
        Assert.AreSame(failure, Assert.ContainsSingle(aggregate.InnerExceptions));
        Assert.IsNull(attempts[0].Result);
        Assert.AreEqual(TimeSpan.Zero, attempts[0].PreviousDelay);

        // A run that cancelled itself gives the OperationCanceledException.
        Assert.AreEqual(2, attempts[1].AttemptNumber);
        OperationCanceledException cancellation = Assert.IsInstanceOfType<OperationCanceledException>(attempts[1].Exception);
        Assert.AreEqual(timeout.Token, cancellation.CancellationToken);
    }

    [TestMethod]
    public async Task WhenThePolicyThrows_TheRunnerStopsAndStopAsyncFaultsWithThatException()
    {
        var policyFailure = new FormatException("The policy failed");
        TaskCompletionSource policyEntered = NewSignal();
        var policy = new RecordingRetryPolicy(_ =>
        {
            policyEntered.TrySetResult();
            throw policyFailure;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(new InvalidOperationException("The run failed"));
            },
            policy);

        await WaitAsync(policyEntered.Task, "the policy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        FormatException actual = await Assert.ThrowsExactlyAsync<FormatException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(policyFailure, actual);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenTheRestartStrategyThrows_TheRunnerStopsAndStopAsyncFaultsWithThatException()
    {
        var strategyFailure = new FormatException("The strategy failed");
        TaskCompletionSource strategyEntered = NewSignal();
        IRetryStrategy strategy = RetryStrategies.FromDelegate(_ =>
        {
            strategyEntered.TrySetResult();
            throw strategyFailure;
        });
        var policy = new RecordingRetryPolicy(true);
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return FailAsynchronously(new InvalidOperationException("The run failed"));
            },
            strategy,
            policy);

        await WaitAsync(strategyEntered.Task, "the strategy to be consulted").ConfigureAwait(false);

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);

        FormatException actual = await Assert.ThrowsExactlyAsync<FormatException>(() => stopTask).ConfigureAwait(false);
        Assert.AreSame(strategyFailure, actual);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenRunIsCalledFromATaskOnASchedulerThatLaterCompletes_AFailureStillRestarts()
    {
        TaskCompletionSource allowFirstRunToFail = NewSignal();
        TaskCompletionSource secondRunStarted = NewSignal();
        int invocations = 0;

        var pair = new ConcurrentExclusiveSchedulerPair();
        ReliableTaskRunner runner = await Task.Factory.StartNew(
            () => ReliableTaskRunner.Run(async ct =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    await allowFirstRunToFail.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("First run failed");
                }

                secondRunStarted.TrySetResult();
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
            }),
            CancellationToken.None,
            TaskCreationOptions.None,
            pair.ExclusiveScheduler).ConfigureAwait(false);

        // The scheduler that was current when Run was called now accepts no more tasks.
        pair.Complete();
        await WaitAsync(pair.Completion, "the scheduler to complete").ConfigureAwait(false);

        allowFirstRunToFail.TrySetResult();
        await WaitAsync(secondRunStarted.Task, "the operation to be restarted").ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
        await WaitAsync(runner.StopAsync(), "the runner to stop").ConfigureAwait(false);
    }

    [TestMethod]
    public void WhenTheRunFunctionReturnsNull_RunThrowsInvalidOperationException()
    {
        var policy = new RecordingRetryPolicy(true);

        Assert.ThrowsExactly<InvalidOperationException>(() => ReliableTaskRunner.Run(_ => null!, policy));
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheRunFunctionReturnsNullOnARestart_ThePolicyReceivesInvalidOperationException()
    {
        TaskCompletionSource secondFailureSeen = NewSignal();
        int consultations = 0;
        var policy = new RecordingRetryPolicy(_ =>
        {
            if (Interlocked.Increment(ref consultations) == 1)
            {
                return true;
            }

            secondFailureSeen.TrySetResult();
            return false;
        });
        int invocations = 0;

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            _ => Interlocked.Increment(ref invocations) == 1
                ? FailAsynchronously(new FormatException("First run failed"))
                : null!,
            policy);

        await WaitAsync(secondFailureSeen.Task, "the policy to see the second failure").ConfigureAwait(false);

        // A null task is a failed run, not a cancelled one, so the policy decides whether to restart it.
        IReadOnlyList<Exception> consulted = policy.Consulted;
        Assert.HasCount(2, consulted);
        Assert.IsInstanceOfType<InvalidOperationException>(consulted[1]);
        Assert.AreEqual(2, Volatile.Read(ref invocations));

        Task stopTask = runner.StopAsync();
        await WaitAsync(stopTask, "the runner to stop").ConfigureAwait(false);
        Assert.AreSame(consulted[1], await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stopTask).ConfigureAwait(false));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Returns a task that completes when the token is cancelled, without throwing.
    /// </summary>
    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource cancelled = NewSignal();
        cancellationToken.Register(() => cancelled.TrySetResult());
        return cancelled.Task;
    }

    /// <summary>
    /// Returns a task that faults asynchronously, as a real long-running operation would.
    /// </summary>
    private static Task FailAsynchronously(Exception exception) => FailAsynchronously(() => throw exception);

    private static async Task FailAsynchronously(Func<object> fail)
    {
        await Task.Yield();
        fail();
    }

    /// <summary>
    /// Waits for a task to finish and fails the test, rather than hanging, if it does not.
    /// </summary>
    /// <returns>How the task ended. The caller asserts it: the time limit is only a safety bound.</returns>
    private static async Task<TaskStatus> WaitAsync(Task task, string description)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);
        if (winner != task)
        {
            Assert.Fail($"Timed out after {Timeout.TotalSeconds} seconds waiting for {description}.");
        }

        return task.Status;
    }

    /// <summary>
    /// Asserts that a task does not complete while the work it waits for is held on a gate.
    /// </summary>
    /// <remarks>
    /// Use this only where no public signal shows that the work that would complete the task too early has ended (for
    /// example the end of the task that <see cref="CancellationTokenSource.CancelAsync"/> returns). The method gives
    /// that work <see cref="SettlePeriod"/> in which to complete the task. A correct runner never completes the task
    /// while the gate is closed, so the period cannot cause a false failure: it only makes a defect visible.
    /// </remarks>
    private static async Task AssertStaysIncompleteAsync(Task task, string description)
    {
        await Task.WhenAny(task, Task.Delay(SettlePeriod)).ConfigureAwait(false);
        Assert.IsFalse(task.IsCompleted, $"Expected {description} not to complete, but it ended as {task.Status}.");
    }

    /// <summary>
    /// A restart strategy that records every attempt it receives, and signals when the inner strategy refuses one.
    /// </summary>
    private sealed class RecordingRestartStrategy(IRetryStrategy inner) : IRetryStrategy
    {
        private readonly Lock sync = new();
        private readonly List<RetryAttempt> attempts = [];
        private readonly TaskCompletionSource refused = NewSignal();

        public Task Refused => this.refused.Task;

        public IReadOnlyList<RetryAttempt> Attempts
        {
            get
            {
                lock (this.sync)
                {
                    return this.attempts.ToArray();
                }
            }
        }

        public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
        {
            lock (this.sync)
            {
                this.attempts.Add(attempt);
            }

            TimeSpan? delay = inner.GetRetryDelay(attempt);
            if (delay is null)
            {
                this.refused.TrySetResult();
            }

            return delay;
        }
    }
}
