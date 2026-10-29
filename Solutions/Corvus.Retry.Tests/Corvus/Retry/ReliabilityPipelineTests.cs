// <copyright file="ReliabilityPipelineTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="ReliabilityPipeline"/> gives the operation a token that the caller and the timeouts cancel, applies the
/// attempt timeout and the total timeout, checks the caller's token before each attempt, awaits the retry callback
/// before the delay, keeps no state between calls, and uses the time provider of its options for every delay and
/// timeout.
/// </summary>
/// <remarks>
/// Each test passes its own fake time provider through the options, so these tests run in parallel with others. An
/// operation either completes synchronously, or waits for a real infinite delay that only its token ends.
/// </remarks>
[TestClass]
public class ReliabilityPipelineTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    // REQ-01
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TheOperationReceivesATokenAndStopsWhenTheCallerCancelsIt(bool withTimeouts)
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(3),
            AttemptTimeout = withTimeouts ? OneHour : null,
            TotalTimeout = withTimeouts ? OneHour : null,
            TimeProvider = fake,
        });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return NeverCompletesAsync(ct);
            },
            cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        CancellationToken token = Assert.ContainsSingle(tokens);
        Assert.IsTrue(token.IsCancellationRequested);
    }

    // REQ-02
    [TestMethod]
    public async Task WithAnAttemptTimeoutOf50Milliseconds_ThePipelineMakesThreeAttemptsThenThrowsRetryTimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), AttemptTimeout = attemptTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            }).AsTask();

        for (int timers = 1; timers <= 3; timers++)
        {
            await fake.WaitForTimersAsync(timers).ConfigureAwait(false);
            Assert.IsFalse(call.IsCompleted);
            fake.Advance(attemptTimeout);
        }

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(3, Volatile.Read(ref invocations));
    }

    // REQ-03
    [TestMethod]
    public async Task AnOperationThatDoesNotStop_GetsOneAttemptWithATotalTimeoutOf100Milliseconds()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), TotalTimeout = totalTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            }).AsTask();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    // REQ-03
    [TestMethod]
    public async Task ADelayOfOneHourWithATotalTimeoutOf200Milliseconds_GivesOneAttempt()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(200);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TotalTimeout = totalTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // One timer for the total timeout, and one for the delay after the first failure.
        Assert.IsFalse(call.IsCompleted);
        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    // REQ-04
    [TestMethod]
    public async Task ACallerCancellationGivesAnOperationCanceledException()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { AttemptTimeout = OneHour, TotalTimeout = OneHour, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsNotInstanceOfType<TimeoutException>(thrown);
    }

    // REQ-04
    [TestMethod]
    public async Task ATimeoutGivesARetryTimeoutExceptionThatIsATimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), AttemptTimeout = attemptTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, Assert.IsInstanceOfType<RetryTimeoutException>(thrown).Timeout);
        Assert.IsInstanceOfType<OperationCanceledException>(thrown.InnerException);
    }

    // REQ-05
    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelayOfOneHour_TheSecondAttemptDoesNotStart()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke()), cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    // REQ-06
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithATokenThatIsAlreadyCancelled_TheOperationIsNotCalled(bool withTimeouts)
    {
        RecordingFakeTimeProvider fake = new();
        var cancelled = new CancellationToken(true);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            AttemptTimeout = withTimeouts ? OneHour : null,
            TotalTimeout = withTimeouts ? OneHour : null,
            TimeProvider = fake,
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    invocations++;
                    return new ValueTask<int>(1);
                },
                cancelled).AsTask()).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    invocations++;
                    return ValueTask.CompletedTask;
                },
                cancelled).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(0, invocations);
    }

    // REQ-08
    [TestMethod]
    public async Task OnePipelineGivesEachOfTwoSequentialCallsItsOwnAttempts()
    {
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });
        var first = new FlakyOperation(FlakyOperation.AlwaysFails);
        var second = new FlakyOperation(FlakyOperation.AlwaysFails);
        var third = new FlakyOperation(2);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(_ => new ValueTask<string>(first.Invoke())).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(_ => new ValueTask<string>(second.Invoke())).AsTask()).ConfigureAwait(false);
        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(third.Invoke())).ConfigureAwait(false);

        Assert.AreEqual(3, first.Invocations);
        Assert.AreEqual(3, second.Invocations);
        Assert.AreEqual("attempt-3", result);
    }

    // REQ-08
    [TestMethod]
    [DataRow(2)]
    [DataRow(FlakyOperation.AlwaysFails)]
    public async Task OnePipelineGivesEachOfEightParallelCallsItsOwnAttempts(int failures)
    {
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });
        FlakyOperation[] operations = [.. Enumerable.Range(0, 8).Select(_ => new FlakyOperation(failures))];

        Task<string>[] calls = [.. operations.Select(op => Task.Run(() => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()))];
        try
        {
            await Task.WhenAll(calls).WaitAsync(WaitLimit).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (failures == FlakyOperation.AlwaysFails)
        {
            // Every call fails. Each call is checked below.
        }

        foreach (FlakyOperation op in operations)
        {
            Assert.AreEqual(3, op.Invocations);
        }

        foreach (Task<string> call in calls)
        {
            if (failures == FlakyOperation.AlwaysFails)
            {
                Assert.IsTrue(call.IsFaulted);
            }
            else
            {
                Assert.AreEqual("attempt-3", await call.ConfigureAwait(false));
            }
        }
    }

    // REQ-12
    [TestMethod]
    public async Task TheCallbackGetsTheAttemptNumbersInOrderAndTheDelayStartsOnlyWhenTheCallbackCompletes()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(2);
        var calls = new List<RetryEventArgs>();
        var firstCallback = new TaskCompletionSource();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Linear(OneHour, 5),
            TimeProvider = fake,
            OnRetry = args =>
            {
                lock (calls)
                {
                    calls.Add(args);
                }

                return args.AttemptNumber == 1 ? new ValueTask(firstCallback.Task) : ValueTask.CompletedTask;
            },
        });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The pipeline waits for the first callback, so no delay has started.
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
        Assert.IsNull(calls[0].Result);
        Assert.AreEqual(OneHour, calls[0].DelayBeforeRetry);
    }

    // REQ-12
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnExceptionFromTheCallbackStopsTheCallBeforeTheDelay(bool throwsSynchronously)
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var callbackFailure = new FormatException("The callback failed");
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Linear(OneHour, 5),
            TimeProvider = fake,
            OnRetry = _ => throwsSynchronously ? throw callbackFailure : ValueTask.FromException(callbackFailure),
        });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreEqual(0, fake.TimersCreated);
    }

    // REQ-13
    [TestMethod]
    [DataRow("Count(1)")]
    [DataRow("DoNotRetry")]
    [DataRow("Constant(0)")]
    public async Task WithZeroRetries_ThePipelineCallsTheOperationOneTime(string strategyName)
    {
        IRetryStrategy strategy = strategyName switch
        {
            "Count(1)" => new Count(1),
            "DoNotRetry" => new DoNotRetry(),
            _ => RetryStrategies.Constant(0, TimeSpan.FromSeconds(1)),
        };
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = strategy, TimeProvider = new RecordingFakeTimeProvider() });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(1, op.Invocations);
    }

    // REQ-14
    [TestMethod]
    public async Task ATestOfADelayOfOneHourTakesLessThanOneSecond()
    {
        var stopwatch = Stopwatch.StartNew();
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 2), TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
        fake.Advance(OneHour);

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    // Spec section 6.2
    [TestMethod]
    public async Task WhenTheCallerCancelsAtTheInstantTheAttemptTimesOut_TheResultIsAnOperationCanceledException()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var policy = new RecordingRetryPolicy(true);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), Policy = policy, AttemptTimeout = attemptTimeout, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsEmpty(policy.Consulted);
    }

    // Spec section 6.2
    [TestMethod]
    public async Task WhenTheTotalTimeoutAndTheAttemptTimeoutExpireAtTheSameInstant_TimeoutIsTheTotalTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan timeout = TimeSpan.FromMilliseconds(100);
        var policy = new RecordingRetryPolicy(true);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(3),
            Policy = policy,
            AttemptTimeout = timeout,
            TotalTimeout = timeout,
            TimeProvider = fake,
        });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(timeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(timeout, thrown.Timeout);

        // An attempt timeout would go to the policy. The total timeout does not.
        Assert.IsEmpty(policy.Consulted);
    }

    // Spec section 6.2
    [TestMethod]
    public async Task ACancellationThatNoSourceCaused_GoesToThePolicy()
    {
        var op = new FlakyOperation(1, _ => new OperationCanceledException(new CancellationToken(true)));
        var policy = new RecordingRetryPolicy(true);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(3),
            Policy = policy,
            AttemptTimeout = OneHour,
            TotalTimeout = OneHour,
            TimeProvider = new RecordingFakeTimeProvider(),
        });

        string result = await pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).ConfigureAwait(false);

        Assert.AreEqual("attempt-2", result);
        Assert.AreSame(op.Thrown[0], Assert.ContainsSingle(policy.Consulted));
    }

    // Spec section 6.2
    [TestMethod]
    public async Task HandleTimeoutExceptionRetriesAnAttemptTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(2),
            Policy = RetryPolicies.Handle<TimeoutException>(),
            AttemptTimeout = attemptTimeout,
            TimeProvider = fake,
        });

        Task<string> call = pipeline.ExecuteAsync(
            ct => Interlocked.Increment(ref invocations) == 1 ? NeverCompletesAsync(ct) : new ValueTask<string>("done")).AsTask();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);
        fake.Advance(attemptTimeout);

        Assert.AreEqual("done", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, Volatile.Read(ref invocations));
    }

    // Spec section 6.2
    [TestMethod]
    public async Task HandleInvalidOperationExceptionDoesNotRetryAnAttemptTimeout()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(2),
            Policy = RetryPolicies.Handle<InvalidOperationException>(),
            AttemptTimeout = attemptTimeout,
            TimeProvider = fake,
        });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                Interlocked.Increment(ref invocations);
                return NeverCompletesAsync(ct);
            }).AsTask();

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(1, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task WhenTheStrategyIsNull_ThePipelineMakesFourAttemptsWithTwoSecondsBeforeEachRetry()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan twoSeconds = TimeSpan.FromSeconds(2);
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        for (int timers = 1; timers <= 3; timers++)
        {
            await fake.WaitForTimersAsync(timers).ConfigureAwait(false);
            Assert.IsFalse(call.IsCompleted);
            fake.Advance(twoSeconds);
        }

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(4, op.Invocations);
        Assert.AreSequenceEqual(new[] { twoSeconds, twoSeconds, twoSeconds }, fake.Delays);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task ContinueOnCapturedContextControlsWhetherTheRetryRunsOnTheCapturedContext(bool continueOnCapturedContext, bool withResult)
    {
        var op = new FlakyOperation(1);
        var context = new ThreadPoolSynchronizationContext();
        var firstAttemptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? onRetryContext = null;
        SynchronizationContext? secondAttemptContext = null;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(2),
            ContinueOnCapturedContext = continueOnCapturedContext,
            OnRetry = _ =>
            {
                onRetryContext = SynchronizationContext.Current;
                return DeferredValueTask.Completed();
            },
        });

        // The first attempt fails on a thread with no context, after the test opens the gate. By then the call has
        // returned, so the pipeline is awaiting the attempt. The second attempt succeeds.
        ValueTask<string> WithResultAsync(CancellationToken cancellationToken)
        {
            string result;
            try
            {
                result = op.Invoke();
            }
            catch (InvalidOperationException ex)
            {
                return DeferredValueTask.FromException<string>(ex, firstAttemptGate.Task);
            }

            secondAttemptContext = SynchronizationContext.Current;
            return new ValueTask<string>(result);
        }

        ValueTask WithoutResultAsync(CancellationToken cancellationToken)
        {
            try
            {
                op.Invoke();
            }
            catch (InvalidOperationException ex)
            {
                return DeferredValueTask.FromException(ex, firstAttemptGate.Task);
            }

            secondAttemptContext = SynchronizationContext.Current;
            return ValueTask.CompletedTask;
        }

        Task call = context.Run(() => withResult
            ? pipeline.ExecuteAsync(WithResultAsync).AsTask()
            : pipeline.ExecuteAsync(WithoutResultAsync).AsTask());
        firstAttemptGate.SetResult();

        await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
        SynchronizationContext? expected = continueOnCapturedContext ? context : null;
        Assert.AreSame(expected, onRetryContext);
        Assert.AreSame(expected, secondAttemptContext);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ContinueOnCapturedContextControlsWhetherTheRetryOfTheGenericPipelineRunsOnTheCapturedContext(bool continueOnCapturedContext)
    {
        var op = new FlakyOperation(1);
        var context = new ThreadPoolSynchronizationContext();
        SynchronizationContext? onRetryContext = null;
        SynchronizationContext? secondAttemptContext = null;
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(2),
            ContinueOnCapturedContext = continueOnCapturedContext,
            OnRetry = _ =>
            {
                onRetryContext = SynchronizationContext.Current;
                return DeferredValueTask.Completed();
            },
        });

        ValueTask<string> AttemptAsync(CancellationToken cancellationToken)
        {
            string result;
            try
            {
                result = op.Invoke();
            }
            catch (InvalidOperationException ex)
            {
                return DeferredValueTask.FromException<string>(ex);
            }

            secondAttemptContext = SynchronizationContext.Current;
            return new ValueTask<string>(result);
        }

        Task<string> call = context.Run(() => pipeline.ExecuteAsync(AttemptAsync).AsTask());

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        SynchronizationContext? expected = continueOnCapturedContext ? context : null;
        Assert.AreSame(expected, onRetryContext);
        Assert.AreSame(expected, secondAttemptContext);
    }

    [TestMethod]
    public void TheOperationWithNoResultCompletesSynchronouslyWhenEveryAttemptDoes()
    {
        var op = new FlakyOperation(2);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        ValueTask call = pipeline.ExecuteAsync(
            _ =>
            {
                op.Invoke();
                return ValueTask.CompletedTask;
            });

        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    public async Task TheOperationWithNoResultIsRetriedAfterADelay()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TimeProvider = fake });

        Task call = pipeline.ExecuteAsync(
            _ =>
            {
                op.Invoke();
                return ValueTask.CompletedTask;
            }).AsTask();

        Assert.IsFalse(call.IsCompleted);
        fake.Advance(OneHour);

        await call.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheOperationWithNoResultFailsEveryTime_TheLastExceptionIsThrown()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    op.Invoke();
                    return ValueTask.CompletedTask;
                }).AsTask()).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    public void EachExecuteAsyncRejectsANullOperation()
    {
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions());

        ArgumentNullException withResult = Assert.ThrowsExactly<ArgumentNullException>(
            () => pipeline.ExecuteAsync((Func<CancellationToken, ValueTask<int>>)null!).AsTask());
        ArgumentNullException withoutResult = Assert.ThrowsExactly<ArgumentNullException>(
            () => pipeline.ExecuteAsync((Func<CancellationToken, ValueTask>)null!).AsTask());

        Assert.AreEqual("operation", withResult.ParamName);
        Assert.AreEqual("operation", withoutResult.ParamName);
    }

    // A real infinite delay that only the token ends, so that no timer from the fake time provider is involved.
    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }
}
