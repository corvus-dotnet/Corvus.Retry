// <copyright file="HedgingTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// With <see cref="ReliabilityPipelineOptions.Hedging"/>, a pipeline starts the next attempt when the delay expires or when an
/// attempt fails; the first outcome that is not a failure wins, and the pipeline cancels the other attempts, does not
/// wait for them, and disposes their results; when every attempt fails, it throws the last exception or returns the last
/// result. Each attempt goes through the circuit breaker and has its own attempt timeout, the total timeout applies to
/// all the attempts, and the fallback receives the outcome of the hedging.
/// </summary>
[TestClass]
public class HedgingTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HedgingDelay = TimeSpan.FromSeconds(2);

    private readonly RecordingFakeTimeProvider fake = new();
    private readonly List<HedgingEventArgs> hedgingEvents = [];

    public static IEnumerable<TimeSpan> DelaysOutOfRange =>
    [
        TimeSpan.FromMilliseconds(-2),
        TimeSpan.FromTicks(-1),
        TimeSpan.MinValue,
        TimeSpan.FromMilliseconds(0xFFFFFFFF),
        TimeSpan.MaxValue,
    ];

    public static IEnumerable<TimeSpan> DelaysInRange =>
    [
        TimeSpan.Zero,
        Timeout.InfiniteTimeSpan,
        TimeSpan.FromTicks(1),
        TimeSpan.FromMilliseconds(0xFFFFFFFE),
    ];

    private IReadOnlyList<HedgingEventArgs> HedgingEvents
    {
        get
        {
            lock (this.hedgingEvents)
            {
                return this.hedgingEvents.ToArray();
            }
        }
    }

    // §6.8
    [TestMethod]
    public async Task WhenAttemptOneIsSlow_AttemptTwoStartsAfterTheDelay_WinsAndAttemptOneIsCancelled()
    {
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        this.fake.Advance(HedgingDelay - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, op.Invocations);

        this.fake.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, op.Invocations);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
        Assert.AreEqual(new HedgingEventArgs(2, null), Assert.ContainsSingle(this.HedgingEvents));
    }

    // §6.8
    [TestMethod]
    public async Task WhenAttemptOneFails_AttemptTwoStartsImmediately()
    {
        var failure = new InvalidOperationException("Attempt 1 failed");
        var op = new ScriptedOperation<string>(Fails<string>(failure), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay });

        // No time passes: attempt 2 does not wait for the delay.
        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(new HedgingEventArgs(2, failure), Assert.ContainsSingle(this.HedgingEvents));
        Assert.AreEqual(0, this.fake.TimersCreated);
    }

    // §6.8
    [TestMethod]
    public async Task WithAZeroDelay_AllTheAttemptsStartAtOnce()
    {
        using CancellationTokenSource cts = new();
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 3 });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();

        // No time passes.
        await op.WhenInvokedAsync(4).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);
        Assert.AreEqual(4, op.Invocations);
        Assert.AreSequenceEqual(
            new[] { new HedgingEventArgs(2, null), new HedgingEventArgs(3, null), new HedgingEventArgs(4, null) },
            this.HedgingEvents);

        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
    }

    // §6.8
    [TestMethod]
    public async Task WithAnInfiniteDelay_TheNextAttemptStartsOnlyAfterAFailure()
    {
        var firstAttempt = new TaskCompletionSource<string>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(firstAttempt.Task), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = Timeout.InfiniteTimeSpan });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        this.fake.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(1, op.Invocations);
        Assert.AreEqual(0, this.fake.TimersCreated);

        var failure = new InvalidOperationException("Attempt 1 failed");
        firstAttempt.SetException(failure);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, op.Invocations);
        Assert.AreEqual(new HedgingEventArgs(2, failure), Assert.ContainsSingle(this.HedgingEvents));
    }

    [TestMethod]
    public async Task TheDelayStartsAgainWhenAnAttemptStartsAfterAFailure()
    {
        using CancellationTokenSource cts = new();
        var firstAttempt = new TaskCompletionSource<string>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(firstAttempt.Task), NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 2 });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();

        // Attempt 1 fails at 1 s, so attempt 2 starts then, and the delay starts again.
        this.fake.Advance(TimeSpan.FromSeconds(1));
        firstAttempt.SetException(new InvalidOperationException("Attempt 1 failed"));
        await op.WhenInvokedAsync(2).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);

        // The first delay would have expired at 2 s.
        this.fake.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, op.Invocations);

        // The second delay expires at 3 s.
        this.fake.Advance(TimeSpan.FromSeconds(1));
        await op.WhenInvokedAsync(3).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);
        Assert.AreSequenceEqual(new[] { HedgingDelay, HedgingDelay }, this.fake.Delays);

        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AfterAnOutcomeWins_NoOtherAttemptStarts()
    {
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 5 });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));

        this.fake.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public async Task WhenAttemptOneSucceeds_NoHedgedAttemptStarts()
    {
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 3 });

        ValueTask<string> call = pipeline.ExecuteAsync(op.InvokeAsync);

        Assert.IsTrue(call.IsCompletedSuccessfully);
        Assert.AreEqual("attempt 1", await call.ConfigureAwait(false));
        this.fake.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(1, op.Invocations);
        Assert.IsEmpty(this.HedgingEvents);
    }

    [TestMethod]
    public async Task WithAZeroDelay_WhenAttemptOneSucceedsSynchronously_NoOtherAttemptStarts()
    {
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 10 });

        Assert.AreEqual("attempt 1", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        Assert.AreEqual(1, op.Invocations);
        Assert.IsEmpty(this.HedgingEvents);
    }

    [TestMethod]
    public async Task WithAZeroDelay_WhenAHedgedAttemptSucceeds_ItWinsAndTheOtherAttemptsAreCancelled()
    {
        // The attempts after the first start on the thread pool, so with a zero delay they all start before the
        // pipeline can read an outcome. The test cannot tell how many of them run.
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"), NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 5 });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        IReadOnlyList<CancellationToken> tokens = op.Tokens;
        Assert.IsTrue(tokens[0].IsCancellationRequested);
        Assert.IsFalse(tokens[1].IsCancellationRequested);
        Assert.IsTrue(tokens.Skip(2).All(t => t.IsCancellationRequested));
        Assert.IsLessThanOrEqualTo(6, op.Invocations);
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileAnEarlierAttemptFails_NoOtherAttemptStarts()
    {
        var firstAttempt = new PendingOutcome<string>();
        var secondAttempt = new PendingOutcome<string>();
        var events = new List<int>();
        var op = new ScriptedOperation<string>(_ => firstAttempt.ValueTask, _ => secondAttempt.ValueTask, Succeeds("attempt 3"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                MaxHedgedAttempts = 3,
                OnHedging = args =>
                {
                    events.Add(args.AttemptNumber);
                    if (args.AttemptNumber == 3)
                    {
                        // Attempt 1 fails and attempt 2 succeeds. Both outcomes exist before the pipeline reads either
                        // of them.
                        firstAttempt.SetException(new InvalidOperationException("Attempt 1 failed"));
                        secondAttempt.SetResult("attempt 2");
                    }

                    return ValueTask.CompletedTask;
                },
            });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, op.Invocations);
        Assert.AreSequenceEqual(new[] { 2, 3 }, events);
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileAnEarlierAttemptIsRejected_NoOtherAttemptStartsAndTheRejectedResultIsDisposed()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        var accepted = new Response(200, log);
        var firstAttempt = new PendingOutcome<Response>();
        var secondAttempt = new PendingOutcome<Response>();
        var events = new List<int>();
        var op = new ScriptedOperation<Response>(
            _ => firstAttempt.ValueTask,
            _ => secondAttempt.ValueTask,
            _ => new ValueTask<Response>(new Response(201, log)));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                MaxHedgedAttempts = 3,
                OnHedging = args =>
                {
                    events.Add(args.AttemptNumber);
                    if (args.AttemptNumber == 3)
                    {
                        firstAttempt.SetResult(rejected);
                        secondAttempt.SetResult(accepted);
                    }

                    return ValueTask.CompletedTask;
                },
            },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500 });

        Task<Response> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreSame(accepted, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, op.Invocations);
        Assert.AreSequenceEqual(new[] { 2, 3 }, events);
        Assert.IsTrue(rejected.Disposed);
        Assert.IsFalse(accepted.Disposed);
    }

    [TestMethod]
    public async Task WhenAHedgedAttemptBlocksBeforeItsFirstAwait_TheNextAttemptStillStartsAfterTheDelay()
    {
        using ManualResetEventSlim thirdAttemptStarted = new();
        var secondAttemptBlocks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptUnblocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new ScriptedOperation<string>(
            NeverCompletesAsync,
            ct =>
            {
                // The attempt blocks its thread until attempt 3 starts.
                secondAttemptBlocks.TrySetResult();
                secondAttemptUnblocked.TrySetResult(thirdAttemptStarted.Wait(WaitLimit));
                return NeverCompletesAsync(ct);
            },
            _ =>
            {
                thirdAttemptStarted.Set();
                return new ValueTask<string>("attempt 3");
            });
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 2 });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttemptBlocks.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 3", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(await secondAttemptUnblocked.Task.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { 2, 3 }, this.HedgingEvents.Select(e => e.AttemptNumber));
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileTheHedgingCallbackRuns_TheNextAttemptDoesNotStart()
    {
        var firstAttempt = new TaskCompletionSource<string>();
        var events = new List<int>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(firstAttempt.Task), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = args =>
                {
                    // Attempt 1 succeeds while the callback for attempt 2 runs.
                    events.Add(args.AttemptNumber);
                    firstAttempt.SetResult("attempt 1");
                    return ValueTask.CompletedTask;
                },
            });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 1", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSequenceEqual(new[] { 2 }, events);
    }

    // §6.8
    [TestMethod]
    public async Task WhenAllTheAttemptsFail_ThePipelineThrowsTheLastExceptionWithItsStackTrace()
    {
        var op = new ScriptedOperation<string>(
            Fails<string>(new InvalidOperationException("Attempt 1 failed")),
            Fails<string>(new InvalidOperationException("Attempt 2 failed")),
            Fails<string>(new InvalidOperationException("Attempt 3 failed")));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { MaxHedgedAttempts = 2 });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual("Attempt 3 failed", thrown.Message);
        Assert.AreEqual(3, op.Invocations);
        Assert.Contains(nameof(ThrowFailure), thrown.StackTrace!);
        Assert.AreSequenceEqual(new[] { 2, 3 }, this.HedgingEvents.Select(e => e.AttemptNumber));
        Assert.AreSequenceEqual(new[] { "Attempt 1 failed", "Attempt 2 failed" }, this.HedgingEvents.Select(e => e.LastException!.Message));
    }

    [TestMethod]
    public async Task WhenAllTheAttemptsFailAndTheLastOutcomeIsAnException_ARejectedResultIsDisposed()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        PendingOutcome<Response>[] attempts = [new(), new(), new()];
        var op = new ScriptedOperation<Response>(_ => attempts[0].ValueTask, _ => attempts[1].ValueTask, _ => attempts[2].ValueTask);
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 2 },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500 });

        Task<Response> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await attempts[1].WhenAwaitedAsync().ConfigureAwait(false);
        await attempts[2].WhenAwaitedAsync().ConfigureAwait(false);

        // Every attempt has started. The rejected result waits, because it could be the last outcome.
        attempts[1].SetException(new InvalidOperationException("Attempt 2 failed"));
        attempts[2].SetResult(rejected);
        Assert.IsFalse(call.IsCompleted);
        Assert.IsFalse(rejected.Disposed);

        var lastFailure = new FormatException("Attempt 1 failed last");
        attempts[0].SetException(lastFailure);

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(lastFailure, thrown);
        Assert.IsTrue(rejected.Disposed);
    }

    // §6.8
    [TestMethod]
    public async Task AResultOfAnAttemptThatLosesAfterTheWinner_IsDisposed()
    {
        var loserCompletion = new TaskCompletionSource<Disposable>();
        var winner = new Disposable("winner");
        var loser = new Disposable("loser");

        // Attempt 1 ignores its token, so it completes after the winner.
        var op = new ScriptedOperation<Disposable>(_ => new ValueTask<Disposable>(loserCompletion.Task), _ => new ValueTask<Disposable>(winner));
        ReliabilityPipeline<Disposable> pipeline = this.NewPipeline<Disposable>(new HedgingOptions { Delay = HedgingDelay });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreSame(winner, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
        Assert.IsFalse(loser.Disposed);

        // The call has returned, and did not wait for attempt 1. This is the one place where a test completes an
        // attempt itself.
        loserCompletion.SetResult(loser);

        await loser.WhenDisposed.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.IsFalse(winner.Disposed);
    }

    // §6.8
    [TestMethod]
    public async Task AResultOfAnAttemptThatCompletedButLost_IsDisposed()
    {
        var first = new Disposable("first");
        var second = new Disposable("second");
        var firstAttempt = new PendingOutcome<Disposable>();
        var secondAttempt = new PendingOutcome<Disposable>();
        var op = new ScriptedOperation<Disposable>(_ => firstAttempt.ValueTask, _ => secondAttempt.ValueTask);
        ReliabilityPipeline<Disposable> pipeline = this.NewPipeline<Disposable>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                MaxHedgedAttempts = 2,
                OnHedging = args =>
                {
                    if (args.AttemptNumber == 3)
                    {
                        // Both attempts complete before the pipeline reads an outcome.
                        firstAttempt.SetResult(first);
                        secondAttempt.SetResult(second);
                    }

                    return ValueTask.CompletedTask;
                },
            });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        // Attempt 1 started first, so it wins.
        Disposable result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSame(first, result);
        Assert.AreEqual(2, op.Invocations);
        Assert.IsFalse(first.Disposed);
        Assert.IsTrue(second.Disposed);
    }

    [TestMethod]
    public async Task AResultOfAnAttemptThatLosesIsDisposedOnce_UnderLoad()
    {
        // Many calls, each with attempts that complete on other threads in any order, so that the attempts race the
        // winner. Each result is returned, or disposed exactly once.
        const int Calls = 200;
        var results = new List<Disposable>();
        int started = 0;
        using SemaphoreSlim ended = new(0);
        var pipeline = new ReliabilityPipeline<Disposable>(new ReliabilityPipelineOptions<Disposable>
        {
            Hedging = new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 3 },
            ShouldRetryResult = r => r.Name == "rejected",
        });

        async ValueTask<Disposable> OperationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref started);
            try
            {
                await Task.Yield();
                if (Random.Shared.Next(4) == 0)
                {
                    throw new InvalidOperationException("Failure");
                }

                var result = new Disposable(Random.Shared.Next(4) == 0 ? "rejected" : "accepted");
                lock (results)
                {
                    results.Add(result);
                }

                return result;
            }
            finally
            {
                ended.Release();
            }
        }

        Task<Disposable>[] calls = Enumerable.Range(0, Calls).Select(_ => pipeline.ExecuteAsync(OperationAsync).AsTask()).ToArray();
        var returned = new HashSet<Disposable>();
        foreach (Task<Disposable> call in calls)
        {
            try
            {
                returned.Add(await call.WaitAsync(WaitLimit).ConfigureAwait(false));
            }
            catch (InvalidOperationException)
            {
                // Every attempt of this call failed.
            }
        }

        // The pipeline does not wait for the attempts that lost. No attempt starts after the calls end, so wait until
        // every attempt that started has ended. Then every result exists.
        for (int i = Volatile.Read(ref started); i > 0; i--)
        {
            Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        }

        Disposable[] all;
        lock (results)
        {
            all = [.. results];
        }

        // An attempt disposes its result just after the operation ends, so wait for that too.
        foreach (Disposable result in all.Where(r => !returned.Contains(r)))
        {
            await result.WhenDisposed.WaitAsync(WaitLimit).ConfigureAwait(false);
        }

        foreach (Disposable result in all)
        {
            bool wasReturned = returned.Contains(result);
            if (!(wasReturned ? result.DisposeCount == 0 : result.DisposeCount == 1))
            {
                Assert.Fail($"A {result.Name} result that was {(wasReturned ? "returned" : "not returned")} was disposed {result.DisposeCount} times.");
            }
        }
    }

    // §6.8
    [TestMethod]
    public async Task TheHedgingActionGeneratorGivesADifferentOperationForAnAttempt_AndNullMeansThePrimaryOperation()
    {
        var log = new List<string>();
        var generatorCalls = new List<int>();
        int primaryCalls = 0;
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan, MaxHedgedAttempts = 2 },
            o => o with
            {
                HedgingActionGenerator = attempt =>
                {
                    generatorCalls.Add(attempt);
                    return attempt == 2
                        ? _ =>
                        {
                            log.Add("generated");
                            throw new InvalidOperationException("The other endpoint failed");
                        }
                    : null;
                },
            });

        string result = await pipeline.ExecuteAsync(
            _ =>
            {
                log.Add("primary");
                return ++primaryCalls == 1 ? throw new InvalidOperationException("Primary failed") : new ValueTask<string>("primary");
            }).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("primary", result);
        Assert.AreSequenceEqual(new[] { "primary", "generated", "primary" }, log);
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, generatorCalls);
    }

    [TestMethod]
    public async Task TheOperationThatTheGeneratorGivesCanWin()
    {
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { HedgingActionGenerator = attempt => attempt == 2 ? _ => new ValueTask<string>("marker") : null });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("marker", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AnExceptionFromTheGenerator_StopsTheCallAndCancelsTheAttempts()
    {
        var generatorFailure = new FormatException("The generator failed");
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { HedgingActionGenerator = attempt => attempt == 2 ? throw generatorFailure : null });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(generatorFailure, thrown);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    // §6.8
    [TestMethod]
    public void HedgingWithAStrategy_GivesAnArgumentException()
    {
        var options = new ReliabilityPipelineOptions { Hedging = new HedgingOptions(), Strategy = new Count(3) };
        var optionsOfT = new ReliabilityPipelineOptions<int> { Hedging = new HedgingOptions(), Strategy = new Count(3) };

        ArgumentException thrown = Assert.ThrowsExactly<ArgumentException>(() => new ReliabilityPipeline(options));
        ArgumentException thrownOfT = Assert.ThrowsExactly<ArgumentException>(() => new ReliabilityPipeline<int>(optionsOfT));

        Assert.AreEqual("Hedging", thrown.ParamName);
        Assert.AreEqual("Hedging", thrownOfT.ParamName);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(11)]
    public void TheConstructorRejectsMaxHedgedAttemptsOutsideOneThruTen(int maxHedgedAttempts)
    {
        var options = new ReliabilityPipelineOptions { Hedging = new HedgingOptions { MaxHedgedAttempts = maxHedgedAttempts } };

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReliabilityPipeline(options));
        ArgumentOutOfRangeException thrownOfT = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int> { Hedging = options.Hedging }));

        Assert.AreEqual("Hedging", thrown.ParamName);
        Assert.AreEqual("Hedging", thrownOfT.ParamName);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(10)]
    public void TheConstructorAcceptsMaxHedgedAttemptsFromOneThruTen(int maxHedgedAttempts)
    {
        _ = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions { MaxHedgedAttempts = maxHedgedAttempts } });
    }

    [TestMethod]
    [DynamicData(nameof(DelaysOutOfRange))]
    public void TheConstructorRejectsADelayOutOfRange(TimeSpan delay)
    {
        var options = new ReliabilityPipelineOptions { Hedging = new HedgingOptions { Delay = delay } };

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReliabilityPipeline(options));

        Assert.AreEqual("Hedging", thrown.ParamName);
    }

    [TestMethod]
    [DynamicData(nameof(DelaysInRange))]
    public void TheConstructorAcceptsZeroInfiniteAndPositiveDelays(TimeSpan delay)
    {
        _ = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions { Delay = delay } });
    }

    [TestMethod]
    public void TheConstructorRejectsANullHedgingPolicy()
    {
        var options = new ReliabilityPipelineOptions<int> { Hedging = new HedgingOptions { Policy = null! } };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new ReliabilityPipeline<int>(options));

        Assert.AreEqual("Hedging", thrown.ParamName);
    }

    [TestMethod]
    public void TheDefaultsAreTheDocumentedValues()
    {
        var hedging = new HedgingOptions();

        Assert.AreEqual(1, hedging.MaxHedgedAttempts);
        Assert.AreEqual(TimeSpan.FromSeconds(2), hedging.Delay);
        Assert.AreSame(AnyExceptionPolicy.Default, hedging.Policy);
        Assert.IsNull(hedging.OnHedging);
        Assert.IsNull(new ReliabilityPipelineOptions().Hedging);
        Assert.IsNull(new ReliabilityPipelineOptions<int>().HedgingActionGenerator);
    }

    // §6.8
    [TestMethod]
    public async Task ACancellationByTheCaller_CancelsAllTheAttemptsAndThrowsAnOperationCanceledException()
    {
        using CancellationTokenSource cts = new();
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 3 });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        this.fake.Advance(HedgingDelay);
        this.fake.Advance(HedgingDelay);
        await op.WhenInvokedAsync(3).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsTrue(op.Tokens.All(t => t.IsCancellationRequested));

        // No attempt starts after the cancellation.
        this.fake.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_DoesNotWaitForAnAttemptThatIgnoresItsToken()
    {
        using CancellationTokenSource cts = new();
        var neverCompletes = new TaskCompletionSource<string>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(neverCompletes.Task));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = Timeout.InfiniteTimeSpan });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        Assert.IsFalse(call.IsCompleted);

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task ATokenThatIsAlreadyCancelled_StartsNoAttempt()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync().ConfigureAwait(false);
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions());

        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(0, op.Invocations);
    }

    // §6.9: the total timeout is outside the hedging.
    [TestMethod]
    public async Task TheTotalTimeoutAppliesToAllTheAttempts()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(3);
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay, MaxHedgedAttempts = 5 },
            o => o with { TotalTimeout = totalTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await op.WhenInvokedAsync(2).ConfigureAwait(false);
        Assert.AreEqual(2, op.Invocations);
        this.fake.Advance(totalTimeout - HedgingDelay);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreEqual(2, op.Invocations);
        Assert.IsTrue(op.Tokens.All(t => t.IsCancellationRequested));
    }

    [TestMethod]
    public async Task TheTotalTimeoutDoesNotWaitForAnAttemptThatIgnoresItsToken()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var neverCompletes = new TaskCompletionSource<string>();
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan },
            o => o with { TotalTimeout = totalTimeout });

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(neverCompletes.Task)).AsTask();
        this.fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
    }

    [TestMethod]
    public async Task EachAttemptHasItsOwnAttemptTimeout()
    {
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(5);
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { AttemptTimeout = attemptTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();

        // Attempt 2 starts at 2 s. Attempt 1 times out at 5 s, attempt 2 at 7 s.
        this.fake.Advance(HedgingDelay);
        await op.WhenInvokedAsync(2).ConfigureAwait(false);
        this.fake.Advance(attemptTimeout - HedgingDelay);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
        Assert.IsFalse(op.Tokens[1].IsCancellationRequested);
        Assert.IsFalse(call.IsCompleted);

        this.fake.Advance(HedgingDelay);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        Assert.AreEqual(new HedgingEventArgs(2, null), Assert.ContainsSingle(this.HedgingEvents));
    }

    [TestMethod]
    public async Task AnAttemptTimeoutIsAFailureThatStartsTheNextAttempt()
    {
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(1);
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan },
            o => o with { AttemptTimeout = attemptTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(attemptTimeout);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        HedgingEventArgs args = Assert.ContainsSingle(this.HedgingEvents);
        Assert.AreEqual(2, args.AttemptNumber);
        Assert.AreEqual(attemptTimeout, Assert.IsInstanceOfType<RetryTimeoutException>(args.LastException).Timeout);
    }

    // §6.9: the circuit breaker is inside the hedging.
    [TestMethod]
    public async Task EachAttemptGoesThroughTheCircuitBreaker()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<string>(
            Fails<string>(new InvalidOperationException("Attempt 1 failed")),
            Fails<string>(new InvalidOperationException("Attempt 2 failed")),
            Succeeds("attempt 3"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { MaxHedgedAttempts = 2 },
            o => o with { CircuitBreaker = breaker });

        string result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt 3", result);
        Assert.AreEqual((3, 2), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnAttemptThatTheEngineCancelsAfterAnotherWins_IsNotACallForTheCircuitBreaker()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay }, o => o with { CircuitBreaker = breaker });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual((1, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnOpenCircuitRejectsEachAttemptWithABrokenCircuitException()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        breaker.Isolate();
        var op = new ScriptedOperation<string>(Succeeds("attempt"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { MaxHedgedAttempts = 2 }, o => o with { CircuitBreaker = breaker });

        await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(() => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The hedging policy accepts the rejection as a failure, so every attempt starts and is rejected.
        Assert.AreEqual(0, op.Invocations);
        Assert.HasCount(2, this.HedgingEvents);
        Assert.IsTrue(this.HedgingEvents.All(e => e.LastException is IsolatedCircuitException));
        Assert.AreEqual((0, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_ReleasesTheTestCallOfAHalfOpenCircuit()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, FailureRatio = 1, TimeProvider = this.fake });
        var opener = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        this.fake.Advance(new CircuitBreakerOptions().BreakDuration);
        using CancellationTokenSource cts = new();
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = Timeout.InfiniteTimeSpan }, o => o with { CircuitBreaker = breaker });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        // Another call can make the test call.
        Assert.AreEqual("success", await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task WithHedging_WhenThePolicyOfTheBreakerThrowsDuringTheTestCall_TheCallThrowsThatExceptionAndTheNextCallMakesTheTest()
    {
        var policyFailure = new FormatException("The policy failed");
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            MinimumThroughput = 2,
            FailureRatio = 1,
            TimeProvider = this.fake,
            Policy = RetryPolicies.FromPredicate(e => e is ArgumentException ? throw policyFailure : true),
        });
        var opener = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        this.fake.Advance(new CircuitBreakerOptions().BreakDuration);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan, Policy = RetryPolicies.Handle<InvalidOperationException>() },
            o => o with { CircuitBreaker = breaker });

        // The test call fails, and the policy of the breaker throws for that failure.
        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => throw new ArgumentException("Test call failed")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(policyFailure, thrown);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        // Another call can make the test call.
        Assert.AreEqual("success", await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task ARejectedResultStartsTheNextAttemptAndIsDisposedAfterOnHedging()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        var accepted = new Response(200, log);
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(rejected), _ => new ValueTask<Response>(accepted));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = args =>
                {
                    log.Add($"on hedging {args.AttemptNumber}");
                    return ValueTask.CompletedTask;
                },
            },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500 });

        Response result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSame(accepted, result);
        Assert.IsFalse(accepted.Disposed);
        Assert.AreSequenceEqual(new[] { "on hedging 2", "dispose 503" }, log);
    }

    [TestMethod]
    public async Task WhenEveryResultIsRejected_ThePipelineReturnsTheLastResultAndDisposesTheOthers()
    {
        var log = new List<string>();
        var responses = new[] { new Response(500, log), new Response(502, log), new Response(503, log) };
        var op = new ScriptedOperation<Response>(
            _ => new ValueTask<Response>(responses[0]),
            _ => new ValueTask<Response>(responses[1]),
            _ => new ValueTask<Response>(responses[2]));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { MaxHedgedAttempts = 2 },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500 });

        Response result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSame(responses[2], result);
        Assert.IsTrue(responses[0].Disposed);
        Assert.IsTrue(responses[1].Disposed);
        Assert.IsFalse(responses[2].Disposed);
        Assert.AreSequenceEqual(new[] { new HedgingEventArgs(2, null), new HedgingEventArgs(3, null) }, this.HedgingEvents);
    }

    [TestMethod]
    public async Task WithAZeroDelay_WhenEveryResultIsRejected_ThePipelineReturnsTheLastOutcome()
    {
        var log = new List<string>();
        var responses = new[] { new Response(500, log), new Response(502, log) };
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(responses[0]), _ => new ValueTask<Response>(responses[1]));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { Delay = TimeSpan.Zero },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500 });

        Response result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        // Both attempts started at once. The pipeline reads the outcomes in the sequence in which the attempts started.
        Assert.AreSame(responses[1], result);
        Assert.IsTrue(responses[0].Disposed);
        Assert.IsFalse(responses[1].Disposed);
    }

    [TestMethod]
    public async Task AnExceptionThatTheHedgingPolicyRefuses_IsThrownImmediatelyAndCancelsTheOtherAttempts()
    {
        var refused = new FormatException("Not a failure");
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Fails<string>(refused));
        var policy = new RecordingRetryPolicy(e => e is InvalidOperationException);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 3, Policy = policy });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(refused, thrown);
        Assert.AreSame(refused, Assert.ContainsSingle(policy.Consulted));
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task TheRetryPolicyDoesNotApplyToHedging()
    {
        var op = new ScriptedOperation<string>(Fails<string>(new InvalidOperationException("Attempt 1 failed")), Succeeds("attempt 2"));
        var retryPolicy = new RecordingRetryPolicy(false);
        int onRetryCalls = 0;
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions(),
            o => o with
            {
                Policy = retryPolicy,
                OnRetry = _ =>
                {
                    Interlocked.Increment(ref onRetryCalls);
                    return ValueTask.CompletedTask;
                },
            });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsEmpty(retryPolicy.Consulted);
        Assert.AreEqual(0, onRetryCalls);
    }

    [TestMethod]
    public async Task AnExceptionFromOnHedging_StopsTheCallAndCancelsTheAttempts()
    {
        var callbackFailure = new FormatException("The callback failed");
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay, OnHedging = _ => throw callbackFailure });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task OnHedgingIsAwaitedBeforeTheNextAttemptStarts()
    {
        var callbackCompletion = new TaskCompletionSource();
        var op = new ScriptedOperation<string>(Fails<string>(new InvalidOperationException("Attempt 1 failed")), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { OnHedging = _ => new ValueTask(callbackCompletion.Task) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.AreEqual(1, op.Invocations);
        callbackCompletion.SetResult();

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public async Task AnExceptionFromShouldRetryResult_StopsTheCall()
    {
        var predicateFailure = new FormatException("The predicate failed");
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions(), o => o with { ShouldRetryResult = _ => throw predicateFailure });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(predicateFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WithHedging_AResultIsDisposedWhenShouldRetryResultThrows()
    {
        var predicateFailure = new FormatException("The predicate failed");
        var result = new Response(200, []);
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(result));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(new HedgingOptions(), o => o with { ShouldRetryResult = _ => throw predicateFailure });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(predicateFailure, thrown);
        Assert.IsTrue(result.Disposed);
    }

    [TestMethod]
    public async Task WithHedging_AResultIsDisposedWhenTheStateChangeCallbackOfTheBreakerThrowsAfterTheAttemptSucceeds()
    {
        var callbackFailure = new FormatException("The callback failed");
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            MinimumThroughput = 2,
            FailureRatio = 1,
            TimeProvider = this.fake,
            OnStateChanged = change => change.To == CircuitState.Closed ? throw callbackFailure : ValueTask.CompletedTask,
        });
        var opener = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        this.fake.Advance(new CircuitBreakerOptions().BreakDuration);
        var result = new Response(200, []);
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(result));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(new HedgingOptions(), o => o with { CircuitBreaker = breaker });

        // The test call succeeds and closes the circuit, and the callback for that change throws.
        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.IsTrue(result.Disposed);
    }

    // §6.9: the fallback is outside the hedging.
    [TestMethod]
    public async Task TheFallbackReceivesTheLastExceptionOfTheHedging()
    {
        var op = new ScriptedOperation<string>(
            Fails<string>(new InvalidOperationException("Attempt 1 failed")),
            Fails<string>(new InvalidOperationException("Attempt 2 failed")));
        FallbackArgs<string>? received = null;
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions(),
            o => o with
            {
                Fallback = new FallbackOptions<string>
                {
                    Action = args =>
                    {
                        received = args;
                        return new ValueTask<string>("fallback");
                    },
                },
            });

        Assert.AreEqual("fallback", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual("Attempt 2 failed", received!.Value.Exception!.Message);
    }

    [TestMethod]
    public async Task TheFallbackReceivesARetryTimeoutExceptionFromTheTotalTimeoutAroundTheHedging()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(3);
        FallbackArgs<string>? received = null;
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions(),
            o => o with
            {
                TotalTimeout = totalTimeout,
                Fallback = new FallbackOptions<string>
                {
                    Action = args =>
                    {
                        received = args;
                        return new ValueTask<string>("fallback");
                    },
                },
            });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        this.fake.Advance(totalTimeout - HedgingDelay);

        Assert.AreEqual("fallback", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(totalTimeout, Assert.IsInstanceOfType<RetryTimeoutException>(received!.Value.Exception).Timeout);
    }

    [TestMethod]
    public async Task TheFallbackReceivesTheLastRejectedResultOfTheHedging()
    {
        var log = new List<string>();
        var responses = new[] { new Response(500, log), new Response(503, log) };
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(responses[0]), _ => new ValueTask<Response>(responses[1]));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions(),
            o => o with
            {
                ShouldRetryResult = r => r.StatusCode >= 500,
                Fallback = new FallbackOptions<Response>
                {
                    ShouldFallbackResult = r => r.StatusCode >= 500,
                    Action = _ => new ValueTask<Response>(new Response(200, log)),
                },
            });

        Response result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSequenceEqual(new[] { "dispose 500", "dispose 503" }, log);
    }

    [TestMethod]
    public async Task ThePipelineWithoutAResultTypeHedgesAnOperationThatReturnsAResult()
    {
        var op = new ScriptedOperation<int>(NeverCompletesAsync<int>, _ => new ValueTask<int>(42));
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions { Delay = HedgingDelay }, TimeProvider = this.fake });

        Task<int> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual(42, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task ThePipelineWithoutAResultTypeHedgesAnOperationThatReturnsNoResult()
    {
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions(), TimeProvider = this.fake });

        await pipeline.ExecuteAsync(
            _ => Interlocked.Increment(ref invocations) == 1 ? throw new InvalidOperationException("Attempt 1 failed") : ValueTask.CompletedTask)
            .AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(2, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public async Task ThePipelineWithoutAResultTypeThrowsTheLastExceptionWhenAllTheAttemptsFail()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions { MaxHedgedAttempts = 3 }, TimeProvider = this.fake });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    op.Invoke();
                    return ValueTask.CompletedTask;
                }).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(4, op.Invocations);
    }

    // REQ-14
    [TestMethod]
    public async Task AOneHourHedgingDelayTakesNoRealTime()
    {
        var stopwatch = Stopwatch.StartNew();
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.FromHours(1) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromHours(1) }, this.fake.Delays);
    }

    // A pipeline with the fake time provider and hedging that records each call to OnHedging, unless the test sets
    // its own callback.
    private ReliabilityPipeline<T> NewPipeline<T>(HedgingOptions hedging, Func<ReliabilityPipelineOptions<T>, ReliabilityPipelineOptions<T>>? configure = null)
    {
        if (hedging.OnHedging is null)
        {
            hedging = hedging with
            {
                OnHedging = args =>
                {
                    lock (this.hedgingEvents)
                    {
                        this.hedgingEvents.Add(args);
                    }

                    return ValueTask.CompletedTask;
                },
            };
        }

        var options = new ReliabilityPipelineOptions<T> { Hedging = hedging, TimeProvider = this.fake };
        return new ReliabilityPipeline<T>(configure is null ? options : configure(options));
    }

    private static Func<CancellationToken, ValueTask<T>> Succeeds<T>(T value) => _ => new ValueTask<T>(value);

    private static Func<CancellationToken, ValueTask<T>> Fails<T>(Exception exception) => _ => ThrowFailure<T>(exception);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<T> ThrowFailure<T>(Exception exception) => throw exception;

    private static ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken) => NeverCompletesAsync<string>(cancellationToken);

    private static async ValueTask<T> NeverCompletesAsync<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return default!;
    }

    // An operation that runs a different behaviour for each attempt (the last behaviour for every later attempt), and
    // records the token that each attempt receives. Attempts after the first start on the thread pool, so a test waits
    // for an invocation with WhenInvokedAsync.
    private sealed class ScriptedOperation<T>(params Func<CancellationToken, ValueTask<T>>[] behaviours)
    {
        private readonly List<CancellationToken> tokens = [];
        private readonly List<TaskCompletionSource> invoked = [];

        public int Invocations
        {
            get
            {
                lock (this.tokens)
                {
                    return this.tokens.Count;
                }
            }
        }

        public IReadOnlyList<CancellationToken> Tokens
        {
            get
            {
                lock (this.tokens)
                {
                    return this.tokens.ToArray();
                }
            }
        }

        // Completes when the behaviour of the invocation with this number (starting at 1) has returned or thrown.
        public Task WhenInvokedAsync(int invocation)
        {
            lock (this.tokens)
            {
                return this.Signal(invocation - 1).Task.WaitAsync(WaitLimit);
            }
        }

        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            int index;
            TaskCompletionSource signal;
            lock (this.tokens)
            {
                index = this.tokens.Count;
                this.tokens.Add(cancellationToken);
                signal = this.Signal(index);
            }

            try
            {
                return behaviours[Math.Min(index, behaviours.Length - 1)](cancellationToken);
            }
            finally
            {
                signal.TrySetResult();
            }
        }

        // Call this method under the lock.
        private TaskCompletionSource Signal(int index)
        {
            while (this.invoked.Count <= index)
            {
                this.invoked.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return this.invoked[index];
        }
    }

    // The outcome of an attempt, which the test sets. WhenAwaitedAsync completes when the attempt waits for the outcome,
    // so a test that sets the outcome after that knows that the attempt completes on the thread that sets it.
    private sealed class PendingOutcome<T> : IValueTaskSource<T>
    {
        private readonly TaskCompletionSource awaited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualResetValueTaskSourceCore<T> core;

        public ValueTask<T> ValueTask => new(this, this.core.Version);

        public Task WhenAwaitedAsync() => this.awaited.Task.WaitAsync(WaitLimit);

        public void SetResult(T result) => this.core.SetResult(result);

        public void SetException(Exception exception) => this.core.SetException(exception);

        public T GetResult(short token) => this.core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => this.core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            this.core.OnCompleted(continuation, state, token, flags);
            this.awaited.TrySetResult();
        }
    }

    // A disposable result that counts its disposals, and signals the first one.
    private sealed class Disposable(string name) : IDisposable
    {
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeCount;

        public string Name => name;

        public int DisposeCount => Volatile.Read(ref this.disposeCount);

        public bool Disposed => this.DisposeCount > 0;

        public Task WhenDisposed => this.disposed.Task;

        public void Dispose()
        {
            Interlocked.Increment(ref this.disposeCount);
            this.disposed.TrySetResult();
        }
    }

    // A disposable result with a status code. It writes its disposal to the log.
    private sealed class Response(int statusCode, List<string> log) : IDisposable
    {
        public int StatusCode => statusCode;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            this.Disposed = true;
            lock (log)
            {
                log.Add($"dispose {statusCode}");
            }
        }
    }
}
