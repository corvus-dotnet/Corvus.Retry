// <copyright file="RetryEngineTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The engine that runs every retry loop: it checks the caller's token before each attempt, applies the attempt
/// timeout and the total timeout, classifies a cancellation by its source (caller, then total timeout, then attempt
/// timeout), awaits the retry callback before the delay, and lets the caller's token and the total timeout cancel a
/// delay.
/// </summary>
/// <remarks>
/// Each test passes its own time provider to the engine, so these tests run in parallel with others. An operation
/// that must wait for a timeout waits for a real infinite delay that only its token ends.
/// </remarks>
[TestClass]
public class RetryEngineTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    [TestMethod]
    public async Task WhenEveryAttemptTimesOut_EachAttemptGetsItsOwnCancelledTokenAndTheLastTimeoutIsThrown()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var tokens = new List<CancellationToken>();
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            policy,
            attemptTimeout: attemptTimeout);

        for (int timers = 1; timers <= 3; timers++)
        {
            await fake.WaitForTimersAsync(timers).ConfigureAwait(false);
            Assert.IsFalse(call.IsCompleted);
            fake.Advance(attemptTimeout);
        }

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.IsInstanceOfType<OperationCanceledException>(thrown.InnerException);
        Assert.HasCount(3, tokens);
        Assert.AreNotEqual(tokens[0], tokens[1]);
        Assert.AreNotEqual(tokens[1], tokens[2]);
        Assert.AreNotEqual(tokens[0], tokens[2]);
        Assert.IsTrue(tokens.TrueForAll(t => t.IsCancellationRequested));

        // The policy can retry an attempt timeout. The strategy has no tries left after the third.
        Assert.HasCount(2, policy.Consulted);
        Assert.IsTrue(policy.Consulted[0] is RetryTimeoutException { Timeout.TotalMilliseconds: 50 });
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task WhenThePolicyRefusesAnAttemptTimeout_TheTimeoutIsThrownAfterOneAttempt()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        int invocations = 0;

        Task<string> call = RunAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            new RecordingRetryPolicy(ex => ex is not TimeoutException),
            attemptTimeout: attemptTimeout);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(1, invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringAnAttempt_TheAttemptIsCancelledAndNotRetried()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        int invocations = 0;
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            },
            fake,
            new Count(3),
            policy,
            totalTimeout: totalTimeout);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsInstanceOfType<OperationCanceledException>(thrown.InnerException);
        Assert.AreEqual(1, invocations);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringADelay_TheDelayIsCancelledAndNoFurtherAttemptStarts()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(200);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), totalTimeout: totalTimeout);

        // One timer for the total timeout, and one for the delay after the first failure.
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsInstanceOfType<OperationCanceledException>(thrown.InnerException);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringAFailedAttempt_TheRetryTimeoutExceptionHasThatFailureAsItsInnerException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var failure = new InvalidOperationException("The attempt failed");
        int invocations = 0;

        Task<string> call = RunAsync<string>(
            _ =>
            {
                invocations++;
                fake.Advance(totalTimeout * 2);
                throw failure;
            },
            fake,
            new Count(3),
            totalTimeout: totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(failure, thrown.InnerException);
        Assert.AreEqual(1, invocations);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpiresDuringTheRetryCallback_TheRetryTimeoutExceptionHasTheLastFailureAsItsInnerException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The strategy has no delay, so the next attempt finds that the total timeout has expired.
        Task<string> call = RunAsync(
            _ => new ValueTask<string>(op.Invoke()),
            fake,
            new Count(3),
            totalTimeout: totalTimeout,
            onRetry: _ =>
            {
                fake.Advance(totalTimeout * 2);
                return ValueTask.CompletedTask;
            });

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelay_TheCallIsCancelledAtOnceAndNoFurtherAttemptStarts()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelayWithATotalTimeout_TheCallIsCancelledRatherThanTimedOut()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 3), totalTimeout: OneHour, cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // The caller token cancels the total timeout source too, but the engine reports the caller cancellation.
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheTokenIsAlreadyCancelled_TheOperationIsNotInvoked()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        var op = new FlakyOperation(0);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Count(3), attemptTimeout: OneHour, totalTimeout: OneHour, cancellationToken: cts.Token)).ConfigureAwait(false);

        Assert.AreEqual(0, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsAtTheInstantTheAttemptTimesOut_TheCallIsCancelledRatherThanTimedOut()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var policy = new RecordingRetryPolicy(true);

        Task<string> call = RunAsync(NeverCompletesAsync, fake, new Count(3), policy, attemptTimeout: attemptTimeout, cancellationToken: cts.Token);

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutAndTheAttemptTimeoutExpireAtTheSameInstant_TheTotalTimeoutIsThrown()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan timeout = TimeSpan.FromMilliseconds(100);

        Task<string> call = RunAsync(NeverCompletesAsync, fake, new Count(3), attemptTimeout: timeout, totalTimeout: timeout);

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(timeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(timeout, thrown.Timeout);
    }

    [TestMethod]
    [DataRow(true, true, true, nameof(CancellationCause.Caller))]
    [DataRow(true, false, false, nameof(CancellationCause.Caller))]
    [DataRow(false, true, true, nameof(CancellationCause.TotalTimeout))]
    [DataRow(false, true, false, nameof(CancellationCause.TotalTimeout))]
    [DataRow(false, false, true, nameof(CancellationCause.AttemptTimeout))]
    [DataRow(false, false, false, nameof(CancellationCause.None))]
    public void ClassifyChoosesTheCallerThenTheTotalTimeoutThenTheAttemptTimeout(bool caller, bool total, bool attempt, string expected)
    {
        using var callerCts = new CancellationTokenSource();
        using var totalCts = new CancellationTokenSource();
        using var attemptCts = new CancellationTokenSource();
        CancelIf(caller, callerCts);
        CancelIf(total, totalCts);
        CancelIf(attempt, attemptCts);

        // The cause is internal, so the test data gives its name.
        Assert.AreEqual(Enum.Parse<CancellationCause>(expected), RetryEngine.Classify(callerCts.Token, totalCts, attemptCts));
    }

    [TestMethod]
    public void WhenThereAreNoTimeoutSources_ClassifyFindsOnlyTheCaller()
    {
        Assert.AreEqual(CancellationCause.None, RetryEngine.Classify(CancellationToken.None, null, null));
        Assert.AreEqual(CancellationCause.Caller, RetryEngine.Classify(new CancellationToken(true), null, null));
    }

    [TestMethod]
    public async Task ACancellationThatNoSourceCaused_IsPassedToThePolicyAndRetried()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1, _ => new OperationCanceledException(new CancellationToken(true)));
        var policy = new RecordingRetryPolicy(true);

        string result = await RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Count(3), policy, attemptTimeout: OneHour, totalTimeout: OneHour).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.AreSame(op.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    [TestMethod]
    public async Task TheOnRetryCallbackGetsTheAttemptNumbersInOrderAndTheDelayStartsOnlyWhenItCompletes()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(2);
        var calls = new List<RetryEventArgs>();
        var firstCallback = new TaskCompletionSource();

        ValueTask OnRetry(RetryEventArgs args)
        {
            lock (calls)
            {
                calls.Add(args);
            }

            return args.AttemptNumber == 1 ? new ValueTask(firstCallback.Task) : ValueTask.CompletedTask;
        }

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 5), onRetry: OnRetry);

        // The engine waits for the first callback, so no delay has started.
        Assert.HasCount(1, calls);
        Assert.AreEqual(0, fake.TimersCreated);
        firstCallback.SetResult();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(OneHour);
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(OneHour);

        Assert.AreEqual("attempt-3", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.HasCount(2, calls);
        Assert.AreEqual(1, calls[0].AttemptNumber);
        Assert.AreEqual(2, calls[0].NextAttemptNumber);
        Assert.AreEqual(2, calls[1].AttemptNumber);
        Assert.AreEqual(3, calls[1].NextAttemptNumber);
        Assert.AreSame(op.Thrown[0], calls[0].LastException);
        Assert.AreSame(op.Thrown[1], calls[1].LastException);
        Assert.AreEqual(OneHour, calls[0].DelayBeforeRetry);
        Assert.AreSequenceEqual(new[] { OneHour, OneHour }, fake.Delays);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task AnExceptionFromTheOnRetryCallbackStopsTheCallBeforeTheDelay()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var callbackFailure = new FormatException("The callback failed");

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, new Linear(OneHour, 5), onRetry: _ => throw callbackFailure)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public async Task TheStrategyGetsTheAttemptNumberTheFailureAndThePreviousDelay()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var strategy = new RecordingStrategy(TimeSpan.Zero, TimeSpan.FromTicks(-5), TimeSpan.FromMilliseconds(1));

        Task<string> call = RunAsync(_ => new ValueTask<string>(op.Invoke()), fake, strategy);

        // A zero delay and a negative delay (limited to zero) need no timer.
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(4, op.Invocations);
        Assert.HasCount(4, strategy.Attempts);
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(i + 1, strategy.Attempts[i].AttemptNumber);
            Assert.AreSame(op.Thrown[i], strategy.Attempts[i].Exception);
            Assert.IsNull(strategy.Attempts[i].Result);
        }

        Assert.AreSequenceEqual(
            new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(1) },
            strategy.Attempts.ConvertAll(a => a.PreviousDelay));
    }

    [TestMethod]
    public async Task WhenTheStrategyRefuses_ThePolicyIsNotConsulted()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var policy = new RecordingRetryPolicy(true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RunAsync(_ => new ValueTask<string>(op.Invoke()), new RecordingFakeTimeProvider(), new DoNotRetry(), policy)).ConfigureAwait(false);

        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public void WhenThereIsNoTimeoutAndTheAttemptSucceedsSynchronously_NoTimerIsCreatedAndTheCallCompletesSynchronously()
    {
        RecordingFakeTimeProvider fake = new();

        ValueTask<string> call = RetryEngine.RunAsync<TestOperation<string>, string>(
            new(_ => new ValueTask<string>("done")), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, fake, null, null, null, null, false);

        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("done", call.Result);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    [TestMethod]
    public async Task WhenAResultIsAFailure_ItIsRetriedAndTheCallbackGetsTheResultWithNoException()
    {
        RecordingFakeTimeProvider fake = new();
        int invocations = 0;
        var calls = new List<RetryEventArgs>();
        var strategy = new RecordingStrategy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        var policy = new RecordingRetryPolicy(true);

        string result = await RunAsync(
            _ => new ValueTask<string>(Interlocked.Increment(ref invocations) < 3 ? "bad" : "good"),
            fake,
            strategy,
            policy,
            onRetry: args =>
            {
                calls.Add(args);
                return ValueTask.CompletedTask;
            },
            isFailure: r => r == "bad").ConfigureAwait(false);

        Assert.AreEqual("good", result);
        Assert.AreEqual(3, invocations);
        Assert.HasCount(2, calls);
        Assert.IsNull(calls[0].LastException);
        Assert.AreEqual("bad", calls[0].Result);
        Assert.AreEqual("bad", strategy.Attempts[0].Result);
        Assert.IsNull(strategy.Attempts[0].Exception);

        // The policy is for exceptions only.
        Assert.IsEmpty(policy.Consulted);
    }

    [TestMethod]
    public async Task WhenTheStrategyHasNoTriesLeftForAResultThatIsAFailure_ThatResultIsReturnedUndisposed()
    {
        RecordingFakeTimeProvider fake = new();
        var results = new List<DisposableResult>();

        DisposableResult result = await RetryEngine.RunAsync<TestOperation<DisposableResult>, DisposableResult>(
            new(
                _ =>
                {
                    var r = new DisposableResult();
                    results.Add(r);
                    return new ValueTask<DisposableResult>(r);
                },
                _ => true),
            CancellationToken.None,
            new Count(3),
            AnyExceptionPolicy.Default,
            fake,
            null,
            null,
            args =>
            {
                // The callback sees the rejected result before it is disposed.
                Assert.IsFalse(((DisposableResult)args.Result!).Disposed);
                return ValueTask.CompletedTask;
            },
            null,
            false).ConfigureAwait(false);

        Assert.HasCount(3, results);
        Assert.AreSame(results[2], result);
        Assert.IsTrue(results[0].Disposed);
        Assert.IsTrue(results[1].Disposed);
        Assert.IsFalse(result.Disposed);
    }

    [TestMethod]
    public async Task TheSynchronousLoopIsCancelledDuringADelay()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        // The synchronous loop blocks its thread for the delay, so it runs on the thread pool.
        Task<string> call = Task.Run(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(new(op.Invoke), cts.Token, new Linear(OneHour, 3), AnyExceptionPolicy.Default, fake, null));

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task TheSynchronousLoopWaitsForAnOnRetryCallbackThatHasNotCompleted()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackResult = new TaskCompletionSource();

        Task<string> call = Task.Run(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(
                new(op.Invoke),
                CancellationToken.None,
                new Count(3),
                AnyExceptionPolicy.Default,
                fake,
                _ =>
                {
                    callbackStarted.SetResult();
                    return new ValueTask(callbackResult.Task);
                }));

        await callbackStarted.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsFalse(call.IsCompleted);
        callbackResult.SetResult();

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WhenTheCallbackOfTheSynchronousRetryAwaits_ACallFromASingleThreadedSynchronizationContextCompletesAndRestoresTheContext()
    {
        var op = new FlakyOperation(1);
        SynchronizationContext? contextInCallback = null;

        // The thread of the context waits for the callback, so a continuation that is posted to the context never runs.
        Task<(string Result, bool ContextIsCurrent)> call = BlockedThreadSynchronizationContext.RunOnBlockedThread(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(
                new(op.Invoke),
                CancellationToken.None,
                new Count(3),
                AnyExceptionPolicy.Default,
                new RecordingFakeTimeProvider(),
                async _ =>
                {
                    contextInCallback = SynchronizationContext.Current;
                    await Task.Yield();
                }));

        (string result, bool contextIsCurrent) = await call.WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.IsNull(contextInCallback);
        Assert.IsTrue(contextIsCurrent);
    }

    [TestMethod]
    public void TheSynchronousLoopRethrowsACancellationThatTheCallerCausedWithoutConsultingThePolicy()
    {
        using var cts = new CancellationTokenSource();
        var policy = new RecordingRetryPolicy(true);
        var op = new FlakyOperation(
            FlakyOperation.AlwaysFails,
            _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });

        OperationCanceledException thrown = Assert.ThrowsExactly<OperationCanceledException>(
            () => RetryEngine.Run<RetryOperations.FuncOperation<string>, string>(new(op.Invoke), cts.Token, new Count(3), policy, new RecordingFakeTimeProvider(), null));

        Assert.AreSame(op.Thrown[0], thrown);
        Assert.IsEmpty(policy.Consulted);
    }

    private static Task<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeProvider timeProvider,
        IRetryStrategy strategy,
        IRetryPolicy? policy = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? totalTimeout = null,
        Func<RetryEventArgs, ValueTask>? onRetry = null,
        CancellationToken cancellationToken = default,
        Func<T, bool>? isFailure = null)
    {
        return RetryEngine.RunAsync<TestOperation<T>, T>(
            new(operation, isFailure),
            cancellationToken,
            strategy,
            policy ?? AnyExceptionPolicy.Default,
            timeProvider,
            attemptTimeout,
            totalTimeout,
            onRetry,
            null,
            false).AsTask();
    }

    // A real infinite delay that only the token ends, so that no timer from the fake time provider is involved.
    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }

    private static void CancelIf(bool cancel, CancellationTokenSource source)
    {
        if (cancel)
        {
            source.Cancel();
        }
    }

    private readonly struct TestOperation<T> : IRetryOperation<T>
    {
        private readonly Func<CancellationToken, ValueTask<T>> operation;
        private readonly Func<T, bool>? isFailure;

        public TestOperation(Func<CancellationToken, ValueTask<T>> operation, Func<T, bool>? isFailure = null)
        {
            this.operation = operation;
            this.isFailure = isFailure;
        }

        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken) => this.operation(cancellationToken);

        public bool IsFailure(T result) => this.isFailure?.Invoke(result) ?? false;
    }

    // Returns the given delays in order, then null, and records every attempt it is asked about.
    private sealed class RecordingStrategy(params TimeSpan[] delays) : IRetryStrategy
    {
        public List<RetryAttempt> Attempts { get; } = [];

        public TimeSpan? GetRetryDelay(in RetryAttempt attempt)
        {
            this.Attempts.Add(attempt);
            return attempt.AttemptNumber <= delays.Length ? delays[attempt.AttemptNumber - 1] : null;
        }
    }

    private sealed class DisposableResult : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }
}
