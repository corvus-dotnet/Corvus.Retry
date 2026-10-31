// <copyright file="ReliabilityPipelineTests.cs" company="Endjin Limited">
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

        // The caller always sees an exception that carries its own token. Without timeouts the operation receives the
        // caller's token, and its own cancellation propagates unchanged. With timeouts it receives a token that the
        // caller's token cancels, and the caller sees its own token rather than the token of the attempt.
        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        CancellationToken token = Assert.ContainsSingle(tokens);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(!withTimeouts, token == cts.Token);
    }

    [TestMethod]
    public async Task WithTimeouts_TheOperationReceivesATokenThatIsNotTheCallersAndThePipelineNeverCancelsTheCallersToken()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan totalTimeout = TimeSpan.FromMilliseconds(100);
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new Count(3),
            AttemptTimeout = OneHour,
            TotalTimeout = totalTimeout,
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

        CancellationToken token = Assert.ContainsSingle(tokens);
        Assert.AreNotEqual(cts.Token, token);
        Assert.IsFalse(token.IsCancellationRequested);

        await fake.WaitForTimersAsync(2).ConfigureAwait(false);
        fake.Advance(totalTimeout);

        // The total timeout cancels the operation's token, and not the caller's.
        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(cts.IsCancellationRequested);
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

        // The total timeout expired between two attempts, so the inner exception is the exception of the last attempt.
        Assert.AreSame(op.LastThrown, thrown.InnerException);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    // REQ-04
    [TestMethod]
    public async Task ACallerCancellationGivesAnOperationCanceledException()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { AttemptTimeout = OneHour, TotalTimeout = OneHour, TimeProvider = fake });

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

        // With timeouts, the operation's own cancellation carries the token of the attempt, which the caller's token
        // cancelled. The caller sees an exception that carries its own token.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreNotEqual(Assert.ContainsSingle(tokens), thrown.CancellationToken);
    }

    // REQ-04
    [TestMethod]
    public async Task ATimeoutGivesARetryTimeoutExceptionThatIsATimeoutException()
    {
        RecordingFakeTimeProvider fake = new();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), AttemptTimeout = attemptTimeout, TimeProvider = fake });
        var cancellations = new List<OperationCanceledException>();

        async ValueTask<string> OperationAsync(CancellationToken cancellationToken)
        {
            lock (tokens)
            {
                tokens.Add(cancellationToken);
            }

            try
            {
                return await NeverCompletesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                lock (cancellations)
                {
                    cancellations.Add(ex);
                }

                throw;
            }
        }

        Task<string> call = pipeline.ExecuteAsync(OperationAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, Assert.IsInstanceOfType<RetryTimeoutException>(thrown).Timeout);

        // The inner exception is the cancellation that the operation threw, by the token of the attempt.
        OperationCanceledException cancellation = Assert.ContainsSingle(cancellations);
        Assert.AreSame(cancellation, thrown.InnerException);
        Assert.AreEqual(Assert.ContainsSingle(tokens), cancellation.CancellationToken);
    }

    // REQ-05
    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelayOfOneHour_TheSecondAttemptDoesNotStart()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke()), cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
        await cts.CancelAsync().ConfigureAwait(false);

        // The call ends without a move of the fake time. Without a total timeout, the delay waits on the caller's token.
        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        fake.Advance(OneHour);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsDuringADelayWithATotalTimeout_TheExceptionCarriesTheCallersToken()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 3), TotalTimeout = OneHour * 10, TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(
            ct =>
            {
                tokens.Add(ct);
                return new ValueTask<string>(op.Invoke());
            },
            cts.Token).AsTask();

        Assert.IsFalse(call.IsCompleted);
        await cts.CancelAsync().ConfigureAwait(false);

        // With a total timeout, the delay waits on a token that the caller's token cancels. The exception the caller
        // sees still carries the caller's own token, not the operation's, so a filter on the caller's token matches.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreNotEqual(Assert.ContainsSingle(tokens), thrown.CancellationToken);
        Assert.AreEqual(1, op.Invocations);
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

        OperationCanceledException withResult = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    invocations++;
                    return new ValueTask<int>(1);
                },
                cancelled).AsTask()).ConfigureAwait(false);

        OperationCanceledException withoutResult = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    invocations++;
                    return ValueTask.CompletedTask;
                },
                cancelled).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(0, invocations);
        Assert.AreEqual(cancelled, withResult.CancellationToken);
        Assert.AreEqual(cancelled, withoutResult.CancellationToken);
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

        for (int i = 0; i < calls.Length; i++)
        {
            if (failures == FlakyOperation.AlwaysFails)
            {
                // Each call throws the last exception of its own operation.
                InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => calls[i]).ConfigureAwait(false);
                Assert.AreSame(operations[i].LastThrown, thrown);
            }
            else
            {
                Assert.AreEqual("attempt-3", await calls[i].ConfigureAwait(false));
            }
        }
    }

    // REQ-08
    [TestMethod]
    public async Task TwoCallsThatOverlapOnOnePipelineEachGetTheirOwnAttemptsCallbacksAndFallback()
    {
        RecordingFakeTimeProvider fake = new();
        var opA = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => new InvalidOperationException($"A attempt {attempt} failed"));
        var opB = new FlakyOperation(FlakyOperation.AlwaysFails, attempt => new InvalidOperationException($"B attempt {attempt} failed"));
        var firstAttemptOfAInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retries = new List<RetryEventArgs>();
        var fallbacks = new List<FallbackArgs<string>>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            TotalTimeout = OneHour,
            TimeProvider = fake,
            OnRetry = args =>
            {
                lock (retries)
                {
                    retries.Add(args);
                }

                return ValueTask.CompletedTask;
            },
            Fallback = new FallbackOptions<string>
            {
                Action = args =>
                {
                    lock (fallbacks)
                    {
                        fallbacks.Add(args);
                    }

                    return new ValueTask<string>($"fallback for {args.Exception!.Message}");
                },
            },
        });

        // The first attempt of call A stays in flight until the test releases it. Its other attempts fail synchronously.
        ValueTask<string> OperationA(CancellationToken cancellationToken)
        {
            try
            {
                return new ValueTask<string>(opA.Invoke());
            }
            catch (InvalidOperationException ex) when (opA.Invocations == 1)
            {
                firstAttemptOfAInFlight.SetResult();
                return DeferredValueTask.FromException<string>(ex, releaseA.Task);
            }
        }

        Task<string> callA = pipeline.ExecuteAsync(OperationA).AsTask();
        await firstAttemptOfAInFlight.Task.WaitAsync(WaitLimit).ConfigureAwait(false);

        // Call B runs from start to end while call A is in flight.
        string resultB = await pipeline.ExecuteAsync(_ => new ValueTask<string>(opB.Invoke())).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.IsFalse(callA.IsCompleted);
        Assert.AreEqual(1, opA.Invocations);
        releaseA.SetResult();
        string resultA = await callA.WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("fallback for B attempt 3 failed", resultB);
        Assert.AreEqual("fallback for A attempt 3 failed", resultA);
        Assert.AreEqual(3, opA.Invocations);
        Assert.AreEqual(3, opB.Invocations);

        // Each call has its own total timeout.
        Assert.AreSequenceEqual(new[] { OneHour, OneHour }, fake.Delays);

        // Each call's callbacks receive that call's own exceptions and attempt numbers.
        Assert.AreSequenceEqual(
            new[] { (opB.Thrown[0], 1), (opB.Thrown[1], 2), (opA.Thrown[0], 1), (opA.Thrown[1], 2) },
            retries.Select(r => (r.LastException!, r.AttemptNumber)).ToArray());
        Assert.HasCount(2, fallbacks);
        Assert.AreSame(opB.LastThrown, fallbacks[0].Exception);
        Assert.AreSame(opA.LastThrown, fallbacks[1].Exception);
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
    public async Task ADelayOfOneHourEndsWhenTheFakeTimeMovesOnOneHour()
    {
        RecordingFakeTimeProvider fake = new();
        var op = new FlakyOperation(1);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Linear(OneHour, 2), TimeProvider = fake });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { OneHour }, fake.Delays);
        fake.Advance(OneHour - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, op.Invocations);
        fake.Advance(TimeSpan.FromTicks(1));

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    // Spec section 6.2
    [TestMethod]
    public async Task WhenTheCallerCancelsAtTheInstantTheAttemptTimesOut_TheResultIsAnOperationCanceledException()
    {
        RecordingFakeTimeProvider fake = new();
        using var cts = new CancellationTokenSource();
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(50);
        var policy = new RecordingRetryPolicy(true);
        var tokens = new List<CancellationToken>();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3), Policy = policy, AttemptTimeout = attemptTimeout, TimeProvider = fake });

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

        await fake.WaitForTimersAsync(1).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        fake.Advance(attemptTimeout);

        // The caller wins over the attempt timeout, and sees an exception that carries its own token.
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreNotEqual(Assert.ContainsSingle(tokens), thrown.CancellationToken);
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
