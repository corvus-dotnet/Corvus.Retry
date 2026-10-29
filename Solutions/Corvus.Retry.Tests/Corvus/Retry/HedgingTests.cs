// <copyright file="HedgingTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
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
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);

        // The first delay would have expired at 2 s.
        this.fake.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, op.Invocations);

        // The second delay expires at 3 s.
        this.fake.Advance(TimeSpan.FromSeconds(1));
        await op.WhenInvokedAsync(3).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);
        Assert.AreSequenceEqual(new[] { HedgingDelay, HedgingDelay }, this.fake.Delays);

        await cts.CancelAsync().ConfigureAwait(false);
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
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
        // The attempts after the first start on the thread pool, so with a zero delay some of them can start before the
        // pipeline reads the outcome of attempt 2. The test cannot tell how many: the generator tells which started.
        using SemaphoreSlim ended = new(0);
        var generatorCalls = new List<int>();
        var op = new ScriptedOperation<string>(
            EndsWith(ended, NeverCompletesAsync),
            EndsWith(ended, Succeeds("attempt 2")),
            EndsWith(ended, NeverCompletesAsync));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 5 },
            o => o with { HedgingActionGenerator = RecordingGenerator<string>(generatorCalls) });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        // The pipeline cancels each attempt that started, other than the winner. Wait until each operation has ended.
        int started = Generated(generatorCalls).Length;
        Assert.IsGreaterThanOrEqualTo(2, started);
        Assert.AreSequenceEqual(Enumerable.Range(1, started), Generated(generatorCalls));
        for (int i = 0; i < op.Invocations; i++)
        {
            Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        }

        IReadOnlyList<CancellationToken> tokens = op.Tokens;
        Assert.IsLessThanOrEqualTo(started, tokens.Count);
        Assert.IsTrue(tokens[0].IsCancellationRequested);
        Assert.IsFalse(tokens[1].IsCancellationRequested);
        Assert.IsTrue(tokens.Skip(2).All(t => t.IsCancellationRequested));
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileAnEarlierAttemptFails_NoOtherAttemptStarts()
    {
        using SemaphoreSlim ended = new(0);
        var generatorCalls = new List<int>();
        var firstAttempt = new PendingOutcome<string>();
        var secondAttempt = new PendingOutcome<string>();
        var events = new List<int>();
        var op = new ScriptedOperation<string>(
            EndsWith(ended, _ => firstAttempt.ValueTask),
            EndsWith(ended, _ => secondAttempt.ValueTask),
            EndsWith(ended, Succeeds("attempt 3")));
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
            },
            o => o with { HedgingActionGenerator = RecordingGenerator<string>(generatorCalls) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));

        // Both attempts that started have ended, and the generator was never asked for attempt 3.
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(0, ended.CurrentCount);
        Assert.AreEqual(2, op.Invocations);
        Assert.AreSequenceEqual(new[] { 1, 2 }, Generated(generatorCalls));
        Assert.AreSequenceEqual(new[] { 2, 3 }, events);
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileAnEarlierAttemptIsRejected_NoOtherAttemptStartsAndTheRejectedResultIsDisposed()
    {
        var log = new List<string>();
        var rejected = new Response(503, log);
        var accepted = new Response(200, log);
        using SemaphoreSlim ended = new(0);
        var generatorCalls = new List<int>();
        var firstAttempt = new PendingOutcome<Response>();
        var secondAttempt = new PendingOutcome<Response>();
        var events = new List<int>();
        var op = new ScriptedOperation<Response>(
            EndsWith(ended, _ => firstAttempt.ValueTask),
            EndsWith(ended, _ => secondAttempt.ValueTask),
            EndsWith(ended, _ => new ValueTask<Response>(new Response(201, log))));
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
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500, HedgingActionGenerator = RecordingGenerator<Response>(generatorCalls) });

        Task<Response> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreSame(accepted, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(0, ended.CurrentCount);
        Assert.AreEqual(2, op.Invocations);
        Assert.AreSequenceEqual(new[] { 1, 2 }, Generated(generatorCalls));
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
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 3", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(await secondAttemptUnblocked.Task.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { 2, 3 }, this.HedgingEvents.Select(e => e.AttemptNumber));
    }

    [TestMethod]
    public async Task WhenAnAttemptWinsWhileTheHedgingCallbackRuns_TheOperationAndTheGeneratorAreNeverInvokedForTheNextAttempt()
    {
        using SemaphoreSlim ended = new(0);
        var generatorCalls = new List<int>();
        var firstAttempt = new TaskCompletionSource<string>();
        var events = new List<int>();
        var op = new ScriptedOperation<string>(EndsWith(ended, _ => new ValueTask<string>(firstAttempt.Task)), EndsWith(ended, Succeeds("attempt 2")));
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
            },
            o => o with { HedgingActionGenerator = RecordingGenerator<string>(generatorCalls) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 1", await call.WaitAsync(WaitLimit).ConfigureAwait(false));

        // Attempt 1 has ended. The generator is asked for an attempt only when the attempt starts, so a call for attempt
        // 2 would show an attempt that started after the winner, whether or not its operation has run yet.
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { 1 }, Generated(generatorCalls));
        Assert.AreEqual(0, ended.CurrentCount);
        Assert.AreEqual(1, op.Invocations);
        Assert.AreSequenceEqual(new[] { 2 }, events);
    }

    // §6.8
    [TestMethod]
    public async Task WhenAllTheAttemptsFail_ThePipelineThrowsTheLastExceptionWithItsStackTrace()
    {
        InvalidOperationException[] failures =
        [
            new("Attempt 1 failed"),
            new("Attempt 2 failed"),
            new("Attempt 3 failed"),
        ];
        var op = new ScriptedOperation<string>(Fails<string>(failures[0]), Fails<string>(failures[1]), Fails<string>(failures[2]));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { MaxHedgedAttempts = 2 });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(failures[2], thrown);
        Assert.AreEqual(3, op.Invocations);
        Assert.Contains(nameof(ThrowFailure), thrown.StackTrace!);
        Assert.AreSequenceEqual(new[] { 2, 3 }, this.HedgingEvents.Select(e => e.AttemptNumber));
        Assert.AreSame(failures[0], this.HedgingEvents[0].LastException);
        Assert.AreSame(failures[1], this.HedgingEvents[1].LastException);
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
        var generatorCalls = new List<int>();
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
            },
            o => o with { HedgingActionGenerator = RecordingGenerator<Disposable>(generatorCalls) });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);
        await secondAttempt.WhenAwaitedAsync().ConfigureAwait(false);
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);

        // Attempt 1 started first, so it wins. Both attempts completed before the call returned.
        Disposable result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreSame(first, result);
        Assert.AreSequenceEqual(new[] { 1, 2 }, Generated(generatorCalls));
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
    public async Task TheConstructorAcceptsMaxHedgedAttemptsFromOneThruTen_AndThePipelineMakesThatManyAttemptsAfterTheFirst(int maxHedgedAttempts)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Hedging = new HedgingOptions { MaxHedgedAttempts = maxHedgedAttempts }, TimeProvider = this.fake });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(
                _ =>
                {
                    op.Invoke();
                    return ValueTask.CompletedTask;
                }).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(maxHedgedAttempts + 1, op.Invocations);
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
    public async Task TheConstructorAcceptsZeroInfiniteAndPositiveDelays_AndThePipelineUsesTheDelay(TimeSpan delay)
    {
        using CancellationTokenSource cts = new();
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = delay });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();

        if (delay == TimeSpan.Zero)
        {
            // Both attempts start at once, with no timer.
            await op.WhenInvokedAsync(2).ConfigureAwait(false);
            Assert.AreEqual(0, this.fake.TimersCreated);
        }
        else if (delay == Timeout.InfiniteTimeSpan)
        {
            // Only a failure starts the next attempt.
            Assert.AreEqual(0, this.fake.TimersCreated);
            Assert.AreEqual(1, op.Invocations);
        }
        else
        {
            // The next attempt starts when a timer for the delay fires.
            Assert.AreSequenceEqual(new[] { delay }, this.fake.Delays);
            this.fake.Advance(delay);
            await op.WhenInvokedAsync(2).ConfigureAwait(false);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
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
        await this.fake.WaitForTimersAsync(2).ConfigureAwait(false);
        this.fake.Advance(HedgingDelay);
        await op.WhenInvokedAsync(3).ConfigureAwait(false);
        Assert.AreEqual(3, op.Invocations);

        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
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

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
        Assert.IsFalse(neverCompletes.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ATokenThatIsAlreadyCancelled_StartsNoAttempt()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync().ConfigureAwait(false);
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions());

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
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

        // No attempt failed before the timeout, so there is no inner exception.
        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsNull(thrown.InnerException);
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
        Assert.IsNull(thrown.InnerException);
        Assert.IsFalse(neverCompletes.Task.IsCompleted);
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

        // The inner exception is the cancellation that an attempt timeout caused: the timeout of whichever attempt the
        // pipeline reads last, as both have timed out.
        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(attemptTimeout, thrown.Timeout);
        CancellationToken innerToken = Assert.IsInstanceOfType<OperationCanceledException>(thrown.InnerException).CancellationToken;
        Assert.IsTrue(innerToken == op.Tokens[0] || innerToken == op.Tokens[1]);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnAttemptThatTheEngineCancelsAfterAnotherWins_IsNotACallForTheCircuitBreaker(bool withAttemptTimeout)
    {
        // With an attempt timeout, the token of the attempt has a timer, and the cancellation of a loser could look like an
        // attempt timeout.
        using SemaphoreSlim ended = new(0);
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<string>(EndsWith(ended, NeverCompletesAsync), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { CircuitBreaker = breaker, AttemptTimeout = withAttemptTimeout ? TimeSpan.FromSeconds(30) : null });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));

        // The pipeline does not wait for the loser, so wait until its operation has ended.
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
        Assert.AreEqual((1, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnOpenCircuitRejectsEachAttemptWithABrokenCircuitException()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        breaker.Isolate();
        var op = new ScriptedOperation<string>(Succeeds("attempt"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { MaxHedgedAttempts = 2 }, o => o with { CircuitBreaker = breaker });

        IsolatedCircuitException thrown = await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The hedging policy accepts the rejection as a failure, so every attempt starts and is rejected. Each attempt
        // gets its own rejection, and the call throws the rejection of the last attempt.
        Assert.AreEqual(0, op.Invocations);
        IReadOnlyList<HedgingEventArgs> events = this.HedgingEvents;
        Assert.AreSequenceEqual(new[] { 2, 3 }, events.Select(e => e.AttemptNumber));
        IsolatedCircuitException first = Assert.IsInstanceOfType<IsolatedCircuitException>(events[0].LastException);
        IsolatedCircuitException second = Assert.IsInstanceOfType<IsolatedCircuitException>(events[1].LastException);
        Assert.AreNotSame(first, second);
        Assert.AreNotSame(second, thrown);
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
        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);

        // Another call can make the test call at once: the pipeline released the test call before it threw.
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
        var lastFailure = new InvalidOperationException("Attempt 2 failed");
        var op = new ScriptedOperation<string>(Fails<string>(new InvalidOperationException("Attempt 1 failed")), Fails<string>(lastFailure));
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
        Assert.AreSame(lastFailure, received!.Value.Exception);
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
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(received!.Value.Exception);
        Assert.AreEqual(totalTimeout, timeout.Timeout);
        Assert.IsNull(timeout.InnerException);
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
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.FromHours(1) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreSequenceEqual(new[] { TimeSpan.FromHours(1) }, this.fake.Delays);
    }

    // RetryTimeoutException: when the total timeout expires between attempts, the inner exception is the exception of
    // the last attempt.
    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutExpiresAfterAnAttemptFailed_TheInnerExceptionIsThatFailure()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var failure = new InvalidOperationException("Attempt 1 failed");
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompletion = new TaskCompletionSource();
        var op = new ScriptedOperation<string>(Fails<string>(failure), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                OnHedging = _ =>
                {
                    callbackEntered.TrySetResult();
                    return new ValueTask(callbackCompletion.Task);
                },
            },
            o => o with { TotalTimeout = totalTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await callbackEntered.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        this.fake.Advance(totalTimeout);
        callbackCompletion.SetResult();

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreSame(failure, thrown.InnerException);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutExpiresWhileTheNextAttemptRuns_TheInnerExceptionIsTheFailureOfTheAttemptBefore()
    {
        // The engine and the attempt in flight both see the total timeout, and either can see it first. Repeat the call
        // so that both orders occur: the outcome must not depend on the order.
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        for (int i = 0; i < 50; i++)
        {
            var failure = new InvalidOperationException("Attempt 1 failed");
            var op = new ScriptedOperation<string>(Fails<string>(failure), NeverCompletesAsync);
            ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions(), o => o with { TotalTimeout = totalTimeout });

            Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
            await op.WhenInvokedAsync(2).ConfigureAwait(false);
            this.fake.Advance(totalTimeout);

            RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
            Assert.AreEqual(totalTimeout, thrown.Timeout);
            Assert.AreSame(failure, thrown.InnerException, $"Iteration {i}");
        }
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutExpiresAfterAResultWasRejected_TheInnerExceptionIsNull()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var log = new List<string>();
        var rejected = new Response(503, log);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompletion = new TaskCompletionSource();
        var op = new ScriptedOperation<Response>(
            Fails<Response>(new InvalidOperationException("Attempt 1 failed")),
            _ => new ValueTask<Response>(rejected),
            _ => new ValueTask<Response>(new Response(200, log)));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions
            {
                MaxHedgedAttempts = 2,
                OnHedging = args =>
                {
                    if (args.AttemptNumber < 3)
                    {
                        return ValueTask.CompletedTask;
                    }

                    callbackEntered.TrySetResult();
                    return new ValueTask(callbackCompletion.Task);
                },
            },
            o => o with { TotalTimeout = totalTimeout, ShouldRetryResult = r => r.StatusCode >= 500 });

        // Attempt 1 fails, and then attempt 2 returns a result that is a failure. That result is the last outcome.
        Task<Response> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await callbackEntered.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        this.fake.Advance(totalTimeout);
        callbackCompletion.SetResult();

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsNull(thrown.InnerException);
        Assert.IsTrue(rejected.Disposed);
        Assert.AreEqual(2, op.Invocations);
    }

    // A caller who retries at once after a cancellation is not rejected by the test call of the cancelled call.
    [TestMethod]
    public async Task ACancellationByTheCaller_ReleasesTheTestCallOfAHalfOpenCircuitBeforeTheCallThrows()
    {
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        using CancellationTokenSource cts = new();
        var gate = new TaskCompletionSource<string>();
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = Timeout.InfiniteTimeSpan }, o => o with { CircuitBreaker = breaker });

        // The operation ignores its token, so it is still running when the call throws.
        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(gate.Task), cts.Token).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsFalse(gate.Task.IsCompleted);

        // The next call is the test call, with no wait.
        Assert.AreEqual("success", await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);

        // The abandoned operation ends later, and does not change the circuit.
        gate.SetResult("late");
        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((0, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutExpiresDuringTheTestCall_TheCircuitOpensBeforeTheCallThrows()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        var gate = new TaskCompletionSource<string>();
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan },
            o => o with { CircuitBreaker = breaker, TotalTimeout = totalTimeout });

        // The operation ignores its token, so it is still running when the call throws.
        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(gate.Task)).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        this.fake.Advance(totalTimeout);

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.IsFalse(gate.Task.IsCompleted);

        // The test call failed, so the circuit is open again, with no wait.
        Assert.AreEqual(CircuitState.Open, breaker.State);
        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        gate.SetResult("late");
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    // The exception that the call throws for a cancellation by the caller does not depend on whether the engine or an
    // attempt sees the cancellation first.
    [TestMethod]
    public async Task WhenAnAttemptSeesTheCancellationByTheCallerFirst_TheCallThrowsAnOperationCanceledExceptionForTheCallersToken()
    {
        using CancellationTokenSource cts = new();
        var op = new ScriptedOperation<string>(
            ct =>
            {
                // The attempt sees the cancellation before the engine does.
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return new ValueTask<string>("attempt 1");
            });
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions());

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsEmpty(this.HedgingEvents);
    }

    // Each entry point asks the policy about the exceptions that the operation threw, not the AggregateException
    // around them.
    [TestMethod]
    public async Task WithHedging_AnAggregateExceptionOfExceptionsThatThePolicyHandles_IsAFailureThatStartsTheNextAttempt()
    {
        var aggregate = new AggregateException(new HttpRequestException("Attempt 1 failed"));
        var op = new ScriptedOperation<string>(Fails<string>(aggregate), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay, Policy = RetryPolicies.Handle<HttpRequestException>() });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        // OnHedging receives the exception as the operation threw it.
        HedgingEventArgs args = Assert.ContainsSingle(this.HedgingEvents);
        Assert.AreEqual(2, args.AttemptNumber);
        Assert.AreSame(aggregate, args.LastException);
        Assert.AreEqual(2, op.Invocations);
    }

    [TestMethod]
    public async Task WithHedging_ThePolicyIsAskedAboutEachInnerExceptionOfAnAggregateException()
    {
        var accepted = new HttpRequestException("Accepted");
        var refused = new FormatException("Refused");
        var aggregate = new AggregateException(accepted, new AggregateException(refused));
        var policy = new RecordingRetryPolicy(e => e is HttpRequestException);
        var op = new ScriptedOperation<string>(Fails<string>(aggregate), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay, Policy = policy });

        // One inner exception is refused, so the aggregate is not a failure, and the call throws it at once.
        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(aggregate, thrown);
        Assert.AreSequenceEqual(new Exception[] { accepted, refused }, policy.Consulted);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsEmpty(this.HedgingEvents);
    }

    // F1: each guard that stops an attempt after an outcome wins has a test that only it can satisfy. This one is for the
    // check that an attempt that the call abandoned before the thread pool started it does not run its operation.
    [TestMethod]
    public async Task AnAttemptThatTheCallAbandonsBeforeTheThreadPoolRunsIt_DoesNotInvokeTheOperation()
    {
        // The generator marks the execution context that attempt 2 captures. The thread pool restores that context before
        // the attempt runs, and the change handler of the marker holds that thread until the call has ended. So the call
        // abandons attempt 2 before attempt 2 runs. The handler sees the context restored again when the work item ends.
        var generatorFailure = new FormatException("The generator failed");
        var marker = new object();
        using ManualResetEventSlim callEnded = new();
        var attemptTwoThreadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int heldThread = 0;
        var local = new AsyncLocal<object?>(args =>
        {
            if (!args.ThreadContextChanged)
            {
                return;
            }

            if (ReferenceEquals(args.CurrentValue, marker) && Interlocked.CompareExchange(ref heldThread, Environment.CurrentManagedThreadId, 0) == 0)
            {
                callEnded.Wait(WaitLimit);
            }
            else if (ReferenceEquals(args.PreviousValue, marker) && Volatile.Read(ref heldThread) == Environment.CurrentManagedThreadId)
            {
                attemptTwoThreadDone.TrySetResult();
            }
        });
        var generatorCalls = new List<int>();
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 2 },
            o => o with
            {
                HedgingActionGenerator = attempt =>
                {
                    generatorCalls.Add(attempt);
                    if (attempt == 2)
                    {
                        local.Value = marker;
                    }

                    return attempt == 3 ? throw generatorFailure : null;
                },
            });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(generatorFailure, thrown);
        callEnded.Set();

        // Attempt 2 started, and its work item has ended, but it did not run the operation.
        await attemptTwoThreadDone.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, generatorCalls);
        Assert.AreEqual(1, op.Invocations);
    }

    // The exit-path matrix for the circuit breaker: each exit of an attempt that holds the breaker's permit reports the
    // call once, as a success, a failure, or no call.
    [TestMethod]
    public async Task WithHedging_ARejectedResultIsAFailedCallForTheCircuitBreaker()
    {
        var log = new List<string>();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(new Response(503, log)), _ => new ValueTask<Response>(new Response(200, log)));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { CircuitBreaker = breaker, ShouldRetryResult = r => r.StatusCode >= 500 });

        Response result = await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual((2, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTestCallReturnsARejectedResult_TheCircuitOpensAgain()
    {
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        var log = new List<string>();
        var rejected = new Response(503, log);
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(rejected), _ => new ValueTask<Response>(new Response(200, log)));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { CircuitBreaker = breaker, ShouldRetryResult = r => r.StatusCode >= 500 });

        // The test call fails, so the open circuit rejects attempt 2, and the rejection is the last outcome.
        BrokenCircuitException thrown = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(new CircuitBreakerOptions().BreakDuration, thrown.RetryAfter);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsTrue(rejected.Disposed);
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_IsNotACallForTheCircuitBreaker()
    {
        using SemaphoreSlim ended = new(0);
        using CancellationTokenSource cts = new();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<string>(EndsWith(ended, NeverCompletesAsync));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay }, o => o with { CircuitBreaker = breaker });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        await cts.CancelAsync().ConfigureAwait(false);

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual((0, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WithHedging_WhenShouldRetryResultThrowsDuringTheTestCall_TheCallThrowsThatExceptionAndTheNextCallMakesTheTest()
    {
        var predicateFailure = new FormatException("The predicate failed");
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        var result = new Response(200, []);
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan },
            o => o with { CircuitBreaker = breaker, ShouldRetryResult = r => r == result ? throw predicateFailure : false });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<Response>(result)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(predicateFailure, thrown);
        Assert.IsTrue(result.Disposed);

        // The test call has no outcome, so the circuit is still half-open, and the next call makes the test.
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        Response next = await pipeline.ExecuteAsync(_ => new ValueTask<Response>(new Response(201, []))).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.AreEqual(201, next.StatusCode);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutExpiresDuringTheTestCall_TheAttemptReportsAFailureAndTheCircuitOpensAgain()
    {
        // The pipeline waits in OnHedging, so only the attempt can see the timeout and report it.
        TimeSpan totalTimeout = TimeSpan.FromSeconds(3);
        var changes = new List<CircuitStateChange>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync(
            change =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                if (change.From == CircuitState.HalfOpen)
                {
                    changed.TrySetResult();
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompletion = new TaskCompletionSource();
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = _ =>
                {
                    callbackEntered.TrySetResult();
                    return new ValueTask(callbackCompletion.Task);
                },
            },
            o => o with { CircuitBreaker = breaker, TotalTimeout = totalTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        this.fake.Advance(HedgingDelay);
        await callbackEntered.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        this.fake.Advance(totalTimeout - HedgingDelay);

        // The attempt sees its token cancelled by the total timeout, and reports the test call as a failure.
        await changed.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        callbackCompletion.SetResult();

        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(totalTimeout, thrown.Timeout);
        Assert.AreEqual(CircuitState.Open, breaker.State);
        CircuitStateChange reopened;
        lock (changes)
        {
            reopened = changes[^1];
        }

        Assert.AreEqual(CircuitState.HalfOpen, reopened.From);
        Assert.AreEqual(CircuitState.Open, reopened.To);
        Assert.AreEqual(totalTimeout, Assert.IsInstanceOfType<RetryTimeoutException>(reopened.LastException).Timeout);
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WithHedging_WhenTheTotalTimeoutStopsTheCall_EachAttemptInFlightIsAFailedCall()
    {
        // The operations ignore their tokens, so only the pipeline can report them.
        TimeSpan totalTimeout = TimeSpan.FromSeconds(3);
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var gate = new TaskCompletionSource<string>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(gate.Task));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = TimeSpan.Zero },
            o => o with { CircuitBreaker = breaker, TotalTimeout = totalTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await op.WhenInvokedAsync(2).ConfigureAwait(false);
        this.fake.Advance(totalTimeout);

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual((2, 2), breaker.GetCounts());

        // The operations end later. That does not change the counts.
        gate.SetResult("late");
        Assert.AreEqual((2, 2), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WithHedging_AnOperationThatCancelsItself_IsAFailedCallForTheCircuitBreaker()
    {
        var selfCancellation = new OperationCanceledException("The operation cancelled itself");
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = this.fake });
        var op = new ScriptedOperation<string>(Fails<string>(selfCancellation), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay }, o => o with { CircuitBreaker = breaker });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        // The hedging policy treats the cancellation as any other failure, and so does the breaker.
        Assert.AreSame(selfCancellation, Assert.ContainsSingle(this.HedgingEvents).LastException);
        Assert.AreEqual((2, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WithHedging_WhenOnHedgingThrowsDuringTheTestCall_TheNextCallMakesTheTest()
    {
        var callbackFailure = new FormatException("The callback failed");
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        var gate = new TaskCompletionSource<string>();
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay, OnHedging = _ => throw callbackFailure },
            o => o with { CircuitBreaker = breaker });

        // The operation ignores its token, so it is still running when the call throws.
        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(gate.Task)).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        this.fake.Advance(HedgingDelay);

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(callbackFailure, thrown);

        Assert.AreEqual("success", await pipeline.ExecuteAsync(_ => new ValueTask<string>("success")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
        gate.SetResult("late");
    }

    [TestMethod]
    public async Task WithHedging_WhileAttemptOneMakesTheTestCall_TheHalfOpenCircuitRejectsAttemptTwo()
    {
        CircuitBreaker breaker = await this.NewCircuitThatIsReadyForATestCallAsync().ConfigureAwait(false);
        var firstAttempt = new TaskCompletionSource<string>();
        var op = new ScriptedOperation<string>(_ => new ValueTask<string>(firstAttempt.Task), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay }, o => o with { CircuitBreaker = breaker });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        // Attempt 2 is rejected without a call, and is a failure for the hedging. Attempt 1 is still the test call.
        HedgingEventArgs args = Assert.ContainsSingle(this.HedgingEvents);
        Assert.AreEqual(2, args.AttemptNumber);
        Assert.IsNull(args.LastException);
        await this.WaitUntilAsync(() => breaker.State == CircuitState.HalfOpen && op.Invocations == 1).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);

        firstAttempt.SetResult("attempt 1");

        Assert.AreEqual("attempt 1", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(1, op.Invocations);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    // F3: a rejected result is disposed whatever stops the call during OnHedging.
    [TestMethod]
    [DataRow("throws")]
    [DataRow("cancels the caller and throws")]
    [DataRow("waits while the total timeout expires")]
    public async Task WhenOnHedgingThrowsAfterARejectedResult_TheResultIsDisposedAndTheCallerGetsTheCallbacksException(string callback)
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var callbackFailure = new FormatException("The callback failed");
        using CancellationTokenSource cts = new();
        var callbackCompletion = new TaskCompletionSource();
        var rejected = new Response(503, []);
        var op = new ScriptedOperation<Response>(_ => new ValueTask<Response>(rejected), _ => new ValueTask<Response>(new Response(200, [])));
        ReliabilityPipeline<Response> pipeline = this.NewPipeline<Response>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = _ =>
                {
                    switch (callback)
                    {
                        case "throws":
                            throw callbackFailure;
                        case "cancels the caller and throws":
                            cts.Cancel();
                            cts.Token.ThrowIfCancellationRequested();
                            return ValueTask.CompletedTask;
                        default:
                            return new ValueTask(callbackCompletion.Task);
                    }
                },
            },
            o => o with { ShouldRetryResult = r => r.StatusCode >= 500, TotalTimeout = totalTimeout });

        Task<Response> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        switch (callback)
        {
            case "throws":
                Assert.AreSame(callbackFailure, await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false));
                break;
            case "cancels the caller and throws":
                Assert.AreEqual(cts.Token, (await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false)).CancellationToken);
                break;
            default:
                this.fake.Advance(totalTimeout);
                callbackCompletion.SetResult();
                RetryTimeoutException timeout = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.IsNull(timeout.InnerException);
                break;
        }

        Assert.IsTrue(rejected.Disposed);
        Assert.AreEqual(1, op.Invocations);
    }

    // F6
    [TestMethod]
    public async Task WithHedging_AnExceptionThatTheHedgingPolicyRefuses_GoesToTheFallback()
    {
        var refused = new FormatException("Not a failure");
        var op = new ScriptedOperation<string>(Fails<string>(refused), Succeeds("attempt 2"));
        FallbackArgs<string>? received = null;
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Policy = RetryPolicies.Handle<InvalidOperationException>() },
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
        Assert.AreSame(refused, received!.Value.Exception);
        Assert.AreEqual(1, op.Invocations);
    }

    // F7
    [TestMethod]
    public async Task WithAZeroDelay_WhenAttemptOneFailsSynchronously_OnHedgingForAttemptTwoReceivesThatFailure()
    {
        var failure = new InvalidOperationException("Attempt 1 failed");
        var op = new ScriptedOperation<string>(Fails<string>(failure), Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = TimeSpan.Zero });

        Assert.AreEqual("attempt 2", await pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));

        HedgingEventArgs args = Assert.ContainsSingle(this.HedgingEvents);
        Assert.AreEqual(2, args.AttemptNumber);
        Assert.AreSame(failure, args.LastException);
    }

    // F8: HedgingOptions.Delay says that each later attempt starts on the thread pool, with no SynchronizationContext.
    [TestMethod]
    public async Task WithContinueOnCapturedContext_HedgedAttemptsStartOnTheThreadPoolWithNoSynchronizationContext()
    {
        var seen = new List<(int Attempt, SynchronizationContext? Context, int ThreadId, bool IsThreadPoolThread, TaskScheduler Scheduler)>();
        ReliabilityPipeline<string> pipeline = this.NewThreadRecordingPipeline(seen);

        (string result, PumpingSynchronizationContext context) = await PumpingSynchronizationContext.RunAsync(
            async context => (await pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Unused")).ConfigureAwait(true), context)).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt 3", result);
        var observed = Snapshot(seen);
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, observed.Select(s => s.Attempt));

        // Attempt 1 runs on the caller's thread, with the caller's context.
        Assert.AreSame(context, observed[0].Context);
        Assert.AreEqual(context.ThreadId, observed[0].ThreadId);
        foreach (var later in observed.Skip(1))
        {
            Assert.IsNull(later.Context);
            Assert.IsTrue(later.IsThreadPoolThread);
            Assert.AreSame(TaskScheduler.Default, later.Scheduler);
        }
    }

    [TestMethod]
    public async Task WhenTheCallerRunsOnACustomScheduler_HedgedAttemptsStartOnTheDefaultScheduler()
    {
        var seen = new List<(int Attempt, SynchronizationContext? Context, int ThreadId, bool IsThreadPoolThread, TaskScheduler Scheduler)>();
        ReliabilityPipeline<string> pipeline = this.NewThreadRecordingPipeline(seen);
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

        string result = await Task.Factory.StartNew(
            () => pipeline.ExecuteAsync(_ => throw new InvalidOperationException("Unused")).AsTask(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).Unwrap().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt 3", result);
        var observed = Snapshot(seen);
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, observed.Select(s => s.Attempt));
        Assert.AreSame(scheduler, observed[0].Scheduler);
        foreach (var later in observed.Skip(1))
        {
            Assert.AreSame(TaskScheduler.Default, later.Scheduler);
            Assert.IsTrue(later.IsThreadPoolThread);
            Assert.IsNull(later.Context);
        }
    }

    // F9: a result that the pipeline holds because it could be the last outcome is disposed on every exit.
    [TestMethod]
    [DataRow("a later attempt succeeds")]
    [DataRow("the caller cancels")]
    [DataRow("the total timeout expires")]
    [DataRow("a later attempt throws an exception that the policy refuses")]
    public async Task WhenEveryAttemptHasStarted_AHeldRejectedResultIsDisposedWhenTheCallEnds(string exit)
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        var refused = new FormatException("Not a failure");
        using CancellationTokenSource cts = new();
        var first = new Disposable("rejected");
        var second = new Disposable("rejected");
        var accepted = new Disposable("accepted");
        PendingOutcome<Disposable>[] attempts = [new(), new(), new()];
        var op = new ScriptedOperation<Disposable>(_ => attempts[0].ValueTask, _ => attempts[1].ValueTask, _ => attempts[2].ValueTask);
        ReliabilityPipeline<Disposable> pipeline = this.NewPipeline<Disposable>(
            new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 2, Policy = RetryPolicies.Handle<InvalidOperationException>() },
            o => o with { ShouldRetryResult = r => r.Name == "rejected", TotalTimeout = totalTimeout });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        await attempts[1].WhenAwaitedAsync().ConfigureAwait(false);
        await attempts[2].WhenAwaitedAsync().ConfigureAwait(false);

        // Every attempt has started, so the pipeline holds a rejected result. When it reads the second, it disposes the
        // first: that shows that it now holds the second.
        attempts[0].SetResult(first);
        attempts[1].SetResult(second);
        await first.WhenDisposed.WaitAsync(WaitLimit).ConfigureAwait(false);
        Assert.IsFalse(second.Disposed);

        switch (exit)
        {
            case "a later attempt succeeds":
                attempts[2].SetResult(accepted);
                Assert.AreSame(accepted, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
                Assert.AreEqual(0, accepted.DisposeCount);
                break;
            case "the caller cancels":
                await cts.CancelAsync().ConfigureAwait(false);
                Assert.AreEqual(cts.Token, (await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false)).CancellationToken);
                break;
            case "the total timeout expires":
                this.fake.Advance(totalTimeout);
                Assert.IsNull((await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false)).InnerException);
                break;
            default:
                attempts[2].SetException(refused);
                Assert.AreSame(refused, await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false));
                break;
        }

        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, second.DisposeCount);
    }

    [TestMethod]
    public async Task WhenEveryAttemptHasStarted_ARejectedResultIsDisposedWhenALaterResultIsAlsoRejected()
    {
        var first = new Disposable("rejected");
        var second = new Disposable("rejected");
        PendingOutcome<Disposable>[] attempts = [new(), new()];
        var op = new ScriptedOperation<Disposable>(_ => attempts[0].ValueTask, _ => attempts[1].ValueTask);
        ReliabilityPipeline<Disposable> pipeline = this.NewPipeline<Disposable>(
            new HedgingOptions { Delay = TimeSpan.Zero },
            o => o with { ShouldRetryResult = r => r.Name == "rejected" });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await attempts[1].WhenAwaitedAsync().ConfigureAwait(false);
        attempts[0].SetResult(first);
        attempts[1].SetResult(second);

        // The last rejected result is the outcome. The pipeline disposed the one before it.
        Assert.AreSame(second, await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(0, second.DisposeCount);
    }

    // F11: an outcome that stops the call comes first, and OnHedging does not run for an attempt that cannot start.
    [TestMethod]
    public async Task WhenTheCallerCancelsWhileAnAttemptFails_OnHedgingIsNotCalled()
    {
        using CancellationTokenSource cts = new();
        var generatorCalls = new List<int>();
        var op = new ScriptedOperation<string>(
            _ =>
            {
                // The operation fails with an exception that is not a cancellation, after the caller cancelled.
                cts.Cancel();
                throw new InvalidOperationException("Attempt 1 failed");
            },
            Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = HedgingDelay },
            o => o with { HedgingActionGenerator = RecordingGenerator<string>(generatorCalls) });

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsEmpty(this.HedgingEvents);
        Assert.AreSequenceEqual(new[] { 1 }, Generated(generatorCalls));
        Assert.AreEqual(1, op.Invocations);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsWhileAnEarlierAttemptFails_TheCancellationComesFirstAndThePolicyIsNotAsked()
    {
        // The pipeline continues on the single thread of the context. The test blocks that thread while another thread
        // completes both attempts, so the pipeline reads neither outcome until both exist.
        var failure = new InvalidOperationException("Attempt 1 failed");
        var policy = new RecordingRetryPolicy(false);
        using CancellationTokenSource cts = new();
        PendingOutcome<string>[] attempts = [new(), new()];
        var op = new ScriptedOperation<string>(_ => attempts[0].ValueTask, _ => attempts[1].ValueTask);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions { Delay = TimeSpan.Zero, Policy = policy },
            o => o with { ContinueOnCapturedContext = true });

        OperationCanceledException thrown = await PumpingSynchronizationContext.RunAsync(
            async _ =>
            {
                Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
                await attempts[1].WhenAwaitedAsync().ConfigureAwait(true);
                Assert.IsTrue(
                    Task.Run(
                        () =>
                        {
                            attempts[0].SetException(failure);
                            cts.Cancel();
                            attempts[1].SetException(new OperationCanceledException(cts.Token));
                        }).Wait(WaitLimit));
                return await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call).ConfigureAwait(true);
            }).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.IsEmpty(policy.Consulted);
        Assert.AreEqual(new HedgingEventArgs(2, null), Assert.ContainsSingle(this.HedgingEvents));
    }

    // Behaviours that had no test.
    [TestMethod]
    public async Task AnExceptionFromTheGeneratorForTheFirstAttempt_StopsTheCallBeforeAnyAttemptStarts()
    {
        var generatorFailure = new FormatException("The generator failed");
        var op = new ScriptedOperation<string>(Succeeds("attempt 1"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions(),
            o => o with { HedgingActionGenerator = _ => throw generatorFailure });

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(
            () => pipeline.ExecuteAsync(op.InvokeAsync).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(generatorFailure, thrown);
        Assert.AreEqual(0, op.Invocations);
        Assert.IsEmpty(this.HedgingEvents);
    }

    [TestMethod]
    public async Task AnExceptionThatOnHedgingThrowsAfterItsFirstAwait_StopsTheCallAndCancelsTheAttempts()
    {
        var callbackFailure = new FormatException("The callback failed");
        var op = new ScriptedOperation<string>(NeverCompletesAsync);
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = async _ =>
                {
                    await Task.Yield();
                    throw callbackFailure;
                },
            });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(HedgingDelay);

        FormatException thrown = await Assert.ThrowsExactlyAsync<FormatException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(1, op.Invocations);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task WhenTheCallerCancelsWhileOnHedgingRuns_TheNextAttemptDoesNotStart()
    {
        using CancellationTokenSource cts = new();
        var generatorCalls = new List<int>();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompletion = new TaskCompletionSource();
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(
            new HedgingOptions
            {
                Delay = HedgingDelay,
                OnHedging = _ =>
                {
                    callbackEntered.TrySetResult();
                    return new ValueTask(callbackCompletion.Task);
                },
            },
            o => o with { HedgingActionGenerator = RecordingGenerator<string>(generatorCalls) });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync, cts.Token).AsTask();
        this.fake.Advance(HedgingDelay);
        await callbackEntered.Task.WaitAsync(WaitLimit).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        callbackCompletion.SetResult();

        OperationCanceledException thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(cts.Token, thrown.CancellationToken);
        Assert.AreSequenceEqual(new[] { 1 }, Generated(generatorCalls));
        Assert.AreEqual(1, op.Invocations);
        Assert.IsTrue(op.Tokens[0].IsCancellationRequested);
    }

    [TestMethod]
    public async Task WhenTheTotalTimeoutExpires_ResultsThatTheAttemptsReturnLaterAreDisposed()
    {
        TimeSpan totalTimeout = TimeSpan.FromSeconds(1);
        PendingOutcome<Disposable>[] attempts = [new(), new(), new()];
        Disposable[] results = [new("1"), new("2"), new("3")];
        var op = new ScriptedOperation<Disposable>(_ => attempts[0].ValueTask, _ => attempts[1].ValueTask, _ => attempts[2].ValueTask);
        ReliabilityPipeline<Disposable> pipeline = this.NewPipeline<Disposable>(
            new HedgingOptions { Delay = TimeSpan.Zero, MaxHedgedAttempts = 2 },
            o => o with { TotalTimeout = totalTimeout });

        Task<Disposable> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        await attempts[1].WhenAwaitedAsync().ConfigureAwait(false);
        await attempts[2].WhenAwaitedAsync().ConfigureAwait(false);
        this.fake.Advance(totalTimeout);

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The operations ignore their tokens, and return results after the call has ended.
        for (int i = 0; i < attempts.Length; i++)
        {
            attempts[i].SetResult(results[i]);
        }

        foreach (Disposable result in results)
        {
            await result.WhenDisposed.WaitAsync(WaitLimit).ConfigureAwait(false);
            Assert.AreEqual(1, result.DisposeCount);
        }
    }

    [TestMethod]
    public async Task AnExceptionOfAnAttemptThatLosesAfterTheWinner_IsObservedAndDoesNotReachTheCaller()
    {
        var loserFailure = new InvalidOperationException("The loser failed late");
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.Flatten().InnerExceptions);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using SemaphoreSlim ended = new(0);
            var gate = new TaskCompletionSource();
            var op = new ScriptedOperation<string>(
                EndsWith<string>(ended, async _ =>
                {
                    await gate.Task.ConfigureAwait(false);
                    throw loserFailure;
                }),
                Succeeds("attempt 2"));
            ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = HedgingDelay });

            Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
            this.fake.Advance(HedgingDelay);
            Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));

            gate.SetResult();
            Assert.IsTrue(await ended.WaitAsync(WaitLimit).ConfigureAwait(false));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.DoesNotContain(loserFailure, unobserved);
        }
    }

    [TestMethod]
    public async Task WhenTheDelayIsLongerThanTheAttemptTimeout_TheNextAttemptStartsWhenTheAttemptTimesOut()
    {
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(1);
        TimeSpan delay = TimeSpan.FromSeconds(5);
        var op = new ScriptedOperation<string>(NeverCompletesAsync, Succeeds("attempt 2"));
        ReliabilityPipeline<string> pipeline = this.NewPipeline<string>(new HedgingOptions { Delay = delay }, o => o with { AttemptTimeout = attemptTimeout });

        Task<string> call = pipeline.ExecuteAsync(op.InvokeAsync).AsTask();
        this.fake.Advance(attemptTimeout);

        // The attempt timeout is a failure, so attempt 2 starts at once, long before the delay expires.
        Assert.AreEqual("attempt 2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
        HedgingEventArgs args = Assert.ContainsSingle(this.HedgingEvents);
        Assert.AreEqual(2, args.AttemptNumber);
        RetryTimeoutException timeout = Assert.IsInstanceOfType<RetryTimeoutException>(args.LastException);
        Assert.AreEqual(attemptTimeout, timeout.Timeout);
        Assert.AreEqual(op.Tokens[0], Assert.IsInstanceOfType<OperationCanceledException>(timeout.InnerException).CancellationToken);
    }

    [TestMethod]
    public async Task ThePipelineWithoutAResultTypeHedgesAnOperationThatFailsAsynchronously()
    {
        InvalidOperationException[] failures = [new("Attempt 1 failed"), new("Attempt 2 failed"), new("Attempt 3 failed")];
        var events = new List<HedgingEventArgs>();
        int invocations = 0;
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Hedging = new HedgingOptions
            {
                MaxHedgedAttempts = 2,
                OnHedging = args =>
                {
                    lock (events)
                    {
                        events.Add(args);
                    }

                    return ValueTask.CompletedTask;
                },
            },
            TimeProvider = this.fake,
        });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(
                async _ =>
                {
                    int attempt = Interlocked.Increment(ref invocations);
                    await Task.Yield();
                    throw failures[attempt - 1];
                }).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(failures[2], thrown);
        Assert.AreEqual(3, Volatile.Read(ref invocations));
        lock (events)
        {
            Assert.AreSequenceEqual(new[] { 2, 3 }, events.Select(e => e.AttemptNumber));
            Assert.AreSame(failures[0], events[0].LastException);
            Assert.AreSame(failures[1], events[1].LastException);
        }
    }

    // A circuit breaker that two failures opened, and whose break has ended, so that the next call is the test call.
    private async Task<CircuitBreaker> NewCircuitThatIsReadyForATestCallAsync(Func<CircuitStateChange, ValueTask>? onStateChanged = null)
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 2, FailureRatio = 1, TimeProvider = this.fake, OnStateChanged = onStateChanged });
        var opener = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => opener.ExecuteAsync<int>(_ => throw new InvalidOperationException("Failure")).AsTask()).ConfigureAwait(false);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        this.fake.Advance(new CircuitBreakerOptions().BreakDuration);
        return breaker;
    }

    private static (int Attempt, SynchronizationContext? Context, int ThreadId, bool IsThreadPoolThread, TaskScheduler Scheduler)[] Snapshot(
        List<(int Attempt, SynchronizationContext? Context, int ThreadId, bool IsThreadPoolThread, TaskScheduler Scheduler)> seen)
    {
        lock (seen)
        {
            return [.. seen.OrderBy(s => s.Attempt)];
        }
    }

    // Polls a condition that another thread makes true, with WaitLimit as a safety bound only.
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        TimeSpan waited = TimeSpan.Zero;
        while (!condition())
        {
            Assert.IsLessThan(WaitLimit, waited, "The condition did not become true.");
            await Task.Delay(1).ConfigureAwait(false);
            waited += TimeSpan.FromMilliseconds(1);
        }
    }

    // A pipeline that continues on the captured context, whose attempts 1 and 2 fail synchronously and attempt 3
    // succeeds. Each attempt records where it runs.
    private ReliabilityPipeline<string> NewThreadRecordingPipeline(
        List<(int Attempt, SynchronizationContext? Context, int ThreadId, bool IsThreadPoolThread, TaskScheduler Scheduler)> seen)
    {
        return this.NewPipeline<string>(
            new HedgingOptions { Delay = Timeout.InfiniteTimeSpan, MaxHedgedAttempts = 2 },
            o => o with
            {
                ContinueOnCapturedContext = true,
                HedgingActionGenerator = attempt => _ =>
                {
                    lock (seen)
                    {
                        seen.Add((attempt, SynchronizationContext.Current, Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, TaskScheduler.Current));
                    }

                    return attempt < 3 ? throw new InvalidOperationException($"Attempt {attempt} failed") : new ValueTask<string>("attempt 3");
                },
            });
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

    // Wraps an operation so that it releases the semaphore when it ends, however it ends.
    private static Func<CancellationToken, ValueTask<T>> EndsWith<T>(SemaphoreSlim ended, Func<CancellationToken, ValueTask<T>> operation)
    {
        return async cancellationToken =>
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ended.Release();
            }
        };
    }

    // A generator that records each attempt number that it is asked for, and gives the primary operation.
    private static Func<int, Func<CancellationToken, ValueTask<T>>?> RecordingGenerator<T>(List<int> calls)
    {
        return attempt =>
        {
            lock (calls)
            {
                calls.Add(attempt);
            }

            return null;
        };
    }

    private static int[] Generated(List<int> calls)
    {
        lock (calls)
        {
            return [.. calls];
        }
    }

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
