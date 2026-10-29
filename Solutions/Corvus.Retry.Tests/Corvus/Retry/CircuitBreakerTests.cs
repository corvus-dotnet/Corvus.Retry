// <copyright file="CircuitBreakerTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A <see cref="CircuitBreaker"/> opens when the ratio of failures in its sliding window reaches the limit and the
/// window has enough calls, rejects calls while it is open, lets one test call through after the break, closes or opens
/// again with the outcome of that call, can be isolated and closed by hand, calls its state change callback outside its
/// lock for every change, and does not count cancellations by the caller or exceptions that its policy refuses.
/// </summary>
/// <remarks>
/// Most tests run the breaker through a <see cref="ReliabilityPipeline"/> that makes one attempt, so that each call to the
/// pipeline is one call to the breaker. Each test has its own fake time provider, so the tests run in parallel.
/// </remarks>
[TestClass]
public class CircuitBreakerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider fake = new();
    private readonly List<CircuitStateChange> changes = [];
    private readonly ArithmeticException exitPolicyFailure = new("The policy failed");
    private readonly InvalidCastException resultCheckFailure = new("The result check failed");

    private IReadOnlyList<CircuitStateChange> Changes
    {
        get
        {
            lock (this.changes)
            {
                return this.changes.ToArray();
            }
        }
    }

    [TestMethod]
    public void TheOptionsHaveTheDocumentedDefaults()
    {
        CircuitBreakerOptions options = new();

        Assert.AreEqual(0.1, options.FailureRatio);
        Assert.AreEqual(100, options.MinimumThroughput);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.SamplingDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.BreakDuration);
        Assert.AreSame(AnyExceptionPolicy.Default, options.Policy);
        Assert.IsNull(options.OnStateChanged);
        Assert.AreSame(TimeProvider.System, options.TimeProvider);
        Assert.AreEqual(CircuitState.Closed, new CircuitBreaker(options).State);
    }

    [TestMethod]
    public async Task FiveFailuresInTenCalls_OpenTheCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await SucceedAsync(pipeline, 5).ConfigureAwait(false);
        Exception last = await FailAsync(pipeline, 5).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreSequenceEqual(new[] { new CircuitStateChange(CircuitState.Closed, CircuitState.Open, last) }, this.Changes);
    }

    [TestMethod]
    public async Task NineCallsWithFiveFailures_DoNotOpenTheCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await SucceedAsync(pipeline, 4).ConfigureAwait(false);
        await FailAsync(pipeline, 5).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((9, 5), breaker.GetCounts());
        Assert.IsEmpty(this.Changes);
    }

    [TestMethod]
    public async Task FailuresWithARatioBelowTheLimit_DoNotOpenTheCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await SucceedAsync(pipeline, 6).ConfigureAwait(false);
        await FailAsync(pipeline, 4).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((10, 4), breaker.GetCounts());
    }

    [TestMethod]
    [DataRow(29, CircuitState.Open)]
    [DataRow(30, CircuitState.Closed)]
    public async Task FailuresOutsideTheSamplingWindow_DoNotCount(int secondsLater, CircuitState expected)
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        // Four old failures, then five successes and one failure: five failures in ten calls only if the old ones count.
        await FailAsync(pipeline, 4).ConfigureAwait(false);
        this.fake.Advance(TimeSpan.FromSeconds(secondsLater));
        await SucceedAsync(pipeline, 5).ConfigureAwait(false);
        await FailAsync(pipeline, 1).ConfigureAwait(false);

        Assert.AreEqual(expected, breaker.State);
    }

    [TestMethod]
    [DataRow(31 * TimeSpan.TicksPerSecond, 2, 0, 46 * TimeSpan.TicksPerSecond, 0)]
    [DataRow((30 * TimeSpan.TicksPerSecond) - 1, 6, 4, (45 * TimeSpan.TicksPerSecond) - 1, 2)]
    [DataRow(30 * TimeSpan.TicksPerSecond, 2, 0, 45 * TimeSpan.TicksPerSecond, 0)]
    public async Task FailuresInABucketThatLeavesTheWindow_StopCounting_WhileNewerBucketsStay(
        long firstTicks, int firstCalls, int firstFailures, long lastTicks, int lastCalls)
    {
        // The window of 30 seconds has 10 buckets of 3 seconds. The failures are in the bucket that starts at 0 s, and
        // the successes in the bucket that starts at 15 s. Each bucket leaves the window 30 seconds after it starts.
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        TimeSpan elapsed = TimeSpan.Zero;

        await FailAsync(pipeline, 4).ConfigureAwait(false);
        AdvanceTo(TimeSpan.FromSeconds(15));
        await SucceedAsync(pipeline, 2).ConfigureAwait(false);

        AdvanceTo(TimeSpan.FromTicks(firstTicks));
        Assert.AreEqual((firstCalls, firstFailures), breaker.GetCounts());
        AdvanceTo(TimeSpan.FromSeconds(44));
        Assert.AreEqual((2, 0), breaker.GetCounts());
        AdvanceTo(TimeSpan.FromTicks(lastTicks));
        Assert.AreEqual((lastCalls, 0), breaker.GetCounts());

        void AdvanceTo(TimeSpan time)
        {
            this.fake.Advance(time - elapsed);
            elapsed = time;
        }
    }

    [TestMethod]
    public async Task WhenTheWallClockMovesBack_TheSamplingWindowStillMovesWithTheMonotonicClock()
    {
        var clock = new SplitClockTimeProvider();
        CircuitBreaker breaker = this.NewBreaker(options => options with { TimeProvider = clock });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        // The old failures leave the window only if the breaker ignores the wall clock.
        await FailAsync(pipeline, 4).ConfigureAwait(false);
        clock.MoveWallClock(TimeSpan.FromHours(-1));
        clock.MoveMonotonicClock(TimeSpan.FromSeconds(30));
        await SucceedAsync(pipeline, 5).ConfigureAwait(false);
        await FailAsync(pipeline, 1).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((6, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task WhenTheWallClockMoves_TheBreakStillEndsWithTheMonotonicClock()
    {
        var clock = new SplitClockTimeProvider();
        CircuitBreaker breaker = this.NewBreaker(options => options with { TimeProvider = clock });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);

        // A jump of the wall clock past the break does not end the break.
        clock.MoveWallClock(TimeSpan.FromDays(1));
        clock.MoveMonotonicClock(TimeSpan.FromSeconds(2));
        BrokenCircuitException thrown = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(TimeSpan.FromSeconds(3), thrown.RetryAfter);

        // A move of the wall clock back does not make the break longer.
        clock.MoveWallClock(TimeSpan.FromDays(-2));
        clock.MoveMonotonicClock(TimeSpan.FromSeconds(3));
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task AnOpenCircuit_RejectsCallsWithTheTimeThatRemainsInTheBreak()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        int invocations = 0;

        this.fake.Advance(TimeSpan.FromSeconds(2));
        BrokenCircuitException thrown = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(TimeSpan.FromSeconds(3), thrown.RetryAfter);
        Assert.AreEqual(0, invocations);
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task AfterTheBreak_OneCallGoesThroughAndTheOthersAreRejected_AndASuccessClosesTheCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        Exception opening = await FailAsync(pipeline, 10).ConfigureAwait(false);
        TaskCompletionSource<string> probeResult = new();

        this.fake.Advance(BreakDuration);
        Task<string> probe = pipeline.ExecuteAsync(ct => new ValueTask<string>(probeResult.Task.WaitAsync(ct))).AsTask();

        Assert.IsFalse(probe.IsCompleted);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        BrokenCircuitException rejected = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("other")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsNull(rejected.RetryAfter);

        probeResult.SetResult("probe");

        Assert.AreEqual("probe", await probe.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((0, 0), breaker.GetCounts());
        Assert.AreSequenceEqual(
            new[]
            {
                new CircuitStateChange(CircuitState.Closed, CircuitState.Open, opening),
                new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null),
                new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Closed, null),
            },
            this.Changes);
    }

    [TestMethod]
    public async Task AfterTheBreak_AFailureOfTheTestCallOpensTheCircuitAgain()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);

        this.fake.Advance(BreakDuration);
        Exception probeFailure = await FailAsync(pipeline, 1).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Open, probeFailure), this.Changes[^1]);

        // The new break is a full break from the failure of the test call.
        BrokenCircuitException thrown = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(BreakDuration, thrown.RetryAfter);
    }

    [TestMethod]
    public void TheStateIsOpenUntilTheFirstCallAfterTheBreak()
    {
        CircuitBreaker breaker = this.NewBreaker();
        OpenDirectly(breaker);

        this.fake.Advance(BreakDuration);

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.IsTrue(breaker.TryAcquire(out int probe, out _, out CircuitStateChange? change));
        Assert.AreNotEqual(0, probe);
        Assert.AreEqual(new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null), change);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
    }

    [TestMethod]
    public async Task IsolateRejectsEveryCallWithIsolatedCircuitException_AndCloseClosesTheCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        int invocations = 0;

        breaker.Isolate();

        Assert.AreEqual(CircuitState.Isolated, breaker.State);
        IsolatedCircuitException thrown = await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.IsNull(thrown.RetryAfter);
        Assert.AreEqual(0, invocations);

        // The break duration does not end an isolation.
        this.fake.Advance(BreakDuration);
        await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        breaker.Close();

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            new[]
            {
                new CircuitStateChange(CircuitState.Closed, CircuitState.Isolated, null),
                new CircuitStateChange(CircuitState.Isolated, CircuitState.Closed, null),
            },
            this.Changes);
    }

    [TestMethod]
    public async Task IsolateAndCloseRaiseNoEventWhenTheStateDoesNotChange_AndCloseClearsTheCounts()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 3).ConfigureAwait(false);

        breaker.Close();

        Assert.AreEqual((0, 0), breaker.GetCounts());
        Assert.IsEmpty(this.Changes);

        breaker.Isolate();
        breaker.Isolate();

        Assert.HasCount(1, this.Changes);
    }

    [TestMethod]
    public async Task IsolateAndCloseWorkFromAnOpenCircuit()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        Exception opening = await FailAsync(pipeline, 10).ConfigureAwait(false);

        breaker.Isolate();
        breaker.Close();

        Assert.AreSequenceEqual(
            new[]
            {
                new CircuitStateChange(CircuitState.Closed, CircuitState.Open, opening),
                new CircuitStateChange(CircuitState.Open, CircuitState.Isolated, null),
                new CircuitStateChange(CircuitState.Isolated, CircuitState.Closed, null),
            },
            this.Changes);
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ACancellationByTheCaller_IsNotACallAndNotAFailure()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        for (int i = 0; i < 10; i++)
        {
            using CancellationTokenSource cts = new();
            Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();
            await cts.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        }

        Assert.AreEqual((0, 0), breaker.GetCounts());

        // Five failures in ten calls open the circuit only if the cancellations did not count.
        await SucceedAsync(pipeline, 5).ConfigureAwait(false);
        await FailAsync(pipeline, 5).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public async Task ATestCallThatTheCallerCancels_LetsTheNextCallMakeTheTest()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);

        using CancellationTokenSource cts = new();
        Task<string> probe = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task ATestCallFromBeforeIsolateAndClose_DoesNotCloseTheCircuitWhileALaterTestCallIsInFlight()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<string> staleResult = new();
        Task<string> stale = pipeline.ExecuteAsync(ct => new ValueTask<string>(staleResult.Task.WaitAsync(ct))).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        // The circuit opens again, and a new test call starts while the old one still runs.
        breaker.Isolate();
        breaker.Close();
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<string> currentResult = new();
        Task<string> current = pipeline.ExecuteAsync(ct => new ValueTask<string>(currentResult.Task.WaitAsync(ct))).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        staleResult.SetResult("stale");
        Assert.AreEqual("stale", await stale.WaitAsync(WaitLimit).ConfigureAwait(false));

        // The old test call does not decide the state, and the new test call is still the only test call.
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<string>("other")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        currentResult.SetResult("current");
        Assert.AreEqual("current", await current.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task ATestCallFromBeforeIsolateAndCloseThatTheCallerCancels_DoesNotLetASecondTestCallStart()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        using CancellationTokenSource cts = new();
        Task<string> stale = pipeline.ExecuteAsync(NeverCompletesAsync, cts.Token).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        breaker.Isolate();
        breaker.Close();
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<string> currentResult = new();
        Task<string> current = pipeline.ExecuteAsync(ct => new ValueTask<string>(currentResult.Task.WaitAsync(ct))).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => stale.WaitAsync(WaitLimit)).ConfigureAwait(false);

        // The test call that started after the break is still in flight, so the breaker rejects another call.
        int invocations = 0;
        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual(0, invocations);

        currentResult.SetResult("current");
        Assert.AreEqual("current", await current.WaitAsync(WaitLimit).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    [DataRow(CircuitBreakerExit.Success, CircuitBreakerInterruption.IsolateAndClose)]
    [DataRow(CircuitBreakerExit.Success, CircuitBreakerInterruption.Close)]
    [DataRow(CircuitBreakerExit.Success, CircuitBreakerInterruption.LaterBreakThatHasEnded)]
    [DataRow(CircuitBreakerExit.Failure, CircuitBreakerInterruption.IsolateAndClose)]
    [DataRow(CircuitBreakerExit.Failure, CircuitBreakerInterruption.Close)]
    [DataRow(CircuitBreakerExit.Failure, CircuitBreakerInterruption.LaterBreakThatHasEnded)]
    [DataRow(CircuitBreakerExit.AttemptTimeout, CircuitBreakerInterruption.IsolateAndClose)]
    [DataRow(CircuitBreakerExit.AttemptTimeout, CircuitBreakerInterruption.Close)]
    [DataRow(CircuitBreakerExit.AttemptTimeout, CircuitBreakerInterruption.LaterBreakThatHasEnded)]
    [DataRow(CircuitBreakerExit.TotalTimeout, CircuitBreakerInterruption.IsolateAndClose)]
    [DataRow(CircuitBreakerExit.TotalTimeout, CircuitBreakerInterruption.Close)]
    [DataRow(CircuitBreakerExit.TotalTimeout, CircuitBreakerInterruption.LaterBreakThatHasEnded)]
    [DataRow(CircuitBreakerExit.PolicyRefuses, CircuitBreakerInterruption.IsolateAndClose)]
    [DataRow(CircuitBreakerExit.PolicyRefuses, CircuitBreakerInterruption.Close)]
    [DataRow(CircuitBreakerExit.PolicyRefuses, CircuitBreakerInterruption.LaterBreakThatHasEnded)]
    public async Task AStaleTestCall_DoesNotChangeTheState_AndTheCurrentTestCallDecides(CircuitBreakerExit staleExit, CircuitBreakerInterruption interruption)
    {
        // The stale timeouts are longer than the break, so that they expire only when the test makes them expire.
        TimeSpan staleTimeout = TimeSpan.FromMinutes(1);
        CircuitBreaker breaker = this.NewBreaker(o => o with { Policy = RetryPolicies.Handle<InvalidOperationException>() });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        var stalePipeline = new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int>
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = staleExit == CircuitBreakerExit.AttemptTimeout ? staleTimeout : null,
            TotalTimeout = staleExit == CircuitBreakerExit.TotalTimeout ? staleTimeout : null,
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<int> staleResult = new();
        Task<int> stale = stalePipeline.ExecuteAsync(ct => new ValueTask<int>(staleResult.Task.WaitAsync(ct))).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        if (interruption == CircuitBreakerInterruption.IsolateAndClose)
        {
            breaker.Isolate();
        }

        breaker.Close();
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<string> currentResult = new();
        Task<string>? current = null;
        if (interruption != CircuitBreakerInterruption.LaterBreakThatHasEnded)
        {
            current = pipeline.ExecuteAsync(ct => new ValueTask<string>(currentResult.Task.WaitAsync(ct))).AsTask();
            Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        }

        CircuitState stateBefore = breaker.State;
        int changesBefore = this.Changes.Count;

        switch (staleExit)
        {
            case CircuitBreakerExit.Success:
                staleResult.SetResult(1);
                Assert.AreEqual(1, await stale.WaitAsync(WaitLimit).ConfigureAwait(false));
                break;

            case CircuitBreakerExit.AttemptTimeout:
            case CircuitBreakerExit.TotalTimeout:
                this.fake.Advance(staleTimeout);
                RetryTimeoutException timeout = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => stale.WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.AreEqual(staleTimeout, timeout.Timeout);
                break;

            default:
                // The policy of the breaker accepts an InvalidOperationException, and refuses an ArgumentException.
                Exception staleFailure = staleExit == CircuitBreakerExit.Failure
                    ? new InvalidOperationException("Stale failure")
                    : new ArgumentException("Stale failure that the policy refuses");
                staleResult.SetException(staleFailure);
                Assert.AreSame(staleFailure, await Assert.ThrowsAsync<Exception>(() => stale.WaitAsync(WaitLimit)).ConfigureAwait(false));
                break;
        }

        // The stale test call changes nothing.
        Assert.AreEqual(stateBefore, breaker.State);
        Assert.HasCount(changesBefore, this.Changes);

        if (current is null)
        {
            // The break has ended and no test call is in flight, so the next call is the test call.
            current = pipeline.ExecuteAsync(ct => new ValueTask<string>(currentResult.Task.WaitAsync(ct))).AsTask();
            Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
            Assert.AreEqual(new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null), this.Changes[^1]);
        }
        else
        {
            // The current test call is still the only test call.
            BrokenCircuitException rejected = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
                () => pipeline.ExecuteAsync(_ => new ValueTask<string>("other")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
            Assert.AreEqual("The circuit is half-open, and a test call is in progress.", rejected.Message);
        }

        // The current test call decides the state.
        InvalidOperationException currentFailure = new("Current failure");
        currentResult.SetException(currentFailure);
        Assert.AreSame(currentFailure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => current.WaitAsync(WaitLimit)).ConfigureAwait(false));
        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Open, currentFailure), this.Changes[^1]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ATestCallFromBeforeCloseThatEndsAfterClose_IsNotCountedInTheNewWindow(bool staleFails)
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        Exception opening = await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        TaskCompletionSource<string> staleResult = new();
        Task<string> stale = pipeline.ExecuteAsync(ct => new ValueTask<string>(staleResult.Task.WaitAsync(ct))).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        // The circuit closes while the test call is in flight, and the new window gets one call.
        breaker.Close();
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
        Assert.AreEqual((1, 0), breaker.GetCounts());

        if (staleFails)
        {
            InvalidOperationException staleFailure = new("Stale failure");
            staleResult.SetException(staleFailure);
            Assert.AreSame(
                staleFailure,
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stale.WaitAsync(WaitLimit)).ConfigureAwait(false));
        }
        else
        {
            staleResult.SetResult("stale");
            Assert.AreEqual("stale", await stale.WaitAsync(WaitLimit).ConfigureAwait(false));
        }

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((1, 0), breaker.GetCounts());
        Assert.AreSequenceEqual(
            new[]
            {
                new CircuitStateChange(CircuitState.Closed, CircuitState.Open, opening),
                new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null),
                new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Closed, null),
            },
            this.Changes);
    }

    [TestMethod]
    public async Task AnExceptionThatThePolicyRefuses_IsNotAFailure()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { Policy = RetryPolicies.Handle<TimeoutException>() });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await FailAsync(pipeline, 10).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((10, 0), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnAggregateException_IsAFailureWhenThePolicyAcceptsTheExceptionsInsideIt()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { Policy = RetryPolicies.Handle<HttpRequestException>() });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        AggregateException failure = new(new HttpRequestException("Failure"));

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => pipeline.ExecuteAsync<string>(_ => throw failure).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(failure, thrown);
        Assert.AreEqual((1, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnAggregateExceptionWithNoInnerExceptions_IsPassedToThePolicyItself()
    {
        List<Exception> consulted = [];
        CircuitBreaker breaker = this.NewBreaker(o => o with
        {
            Policy = RetryPolicies.FromPredicate(ex =>
            {
                lock (consulted)
                {
                    consulted.Add(ex);
                }

                return ex is AggregateException;
            }),
        });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        AggregateException failure = new();

        AggregateException thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => pipeline.ExecuteAsync<string>(_ => throw failure).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(failure, thrown);
        Assert.AreSame(failure, Assert.ContainsSingle(consulted));
        Assert.AreEqual((1, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AResultThatIsAFailure_CountsAsAFailure()
    {
        CircuitBreaker breaker = this.NewBreaker();
        var pipeline = new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int>
        {
            Strategy = new DoNotRetry(),
            ShouldRetryResult = static r => r < 0,
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        for (int i = 0; i < 10; i++)
        {
            int result = i % 2 == 0 ? 1 : -1;
            Assert.AreEqual(result, await pipeline.ExecuteAsync(_ => new ValueTask<int>(result)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(new CircuitStateChange(CircuitState.Closed, CircuitState.Open, null), Assert.ContainsSingle(this.Changes));
    }

    [TestMethod]
    public async Task AnAttemptTimeout_CountsAsAFailure()
    {
        CircuitBreaker breaker = this.NewBreaker();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromSeconds(1),
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();
        Assert.IsFalse(call.IsCompleted);
        this.fake.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        Assert.AreEqual((1, 1), breaker.GetCounts());
    }

    [TestMethod]
    public async Task AnAttemptTimeoutCountsAsAFailure_WhenThePolicyOfTheBreakerDoesNotHandleTimeouts()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { MinimumThroughput = 2, Policy = RetryPolicies.Handle<HttpRequestException>() });
        ReliabilityPipeline pipeline = this.NewPipelineWithAttemptTimeout(breaker);

        for (int i = 0; i < 2; i++)
        {
            Task<string> call = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();
            this.fake.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.IsInstanceOfType<RetryTimeoutException>(Assert.ContainsSingle(this.Changes).LastException);
    }

    [TestMethod]
    public async Task ATestCallThatTimesOut_OpensTheCircuitAgain_WhenThePolicyDoesNotHandleTimeouts()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { Policy = RetryPolicies.Handle<InvalidOperationException>() });
        await FailAsync(this.NewPipeline(breaker), 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        ReliabilityPipeline pipeline = this.NewPipelineWithAttemptTimeout(breaker);

        Task<string> probe = pipeline.ExecuteAsync(NeverCompletesAsync).AsTask();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        this.fake.Advance(TimeSpan.FromSeconds(1));
        RetryTimeoutException thrown = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => probe.WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Open, thrown), this.Changes[^1]);
    }

    [TestMethod]
    [DataRow(CircuitBreakerExit.Success, 1, 0)]
    [DataRow(CircuitBreakerExit.Failure, 1, 1)]
    [DataRow(CircuitBreakerExit.ResultRejected, 1, 1)]
    [DataRow(CircuitBreakerExit.ResultCheckThrows, 0, 0)]
    [DataRow(CircuitBreakerExit.AttemptTimeout, 1, 1)]
    [DataRow(CircuitBreakerExit.TotalTimeout, 1, 1)]
    [DataRow(CircuitBreakerExit.PolicyRefuses, 1, 0)]
    [DataRow(CircuitBreakerExit.PolicyThrows, 0, 0)]
    [DataRow(CircuitBreakerExit.OperationCancelsItself, 1, 1)]
    [DataRow(CircuitBreakerExit.CallerCancels, 0, 0)]
    public async Task EachWayThatACallEndsInAClosedCircuit_IsCountedAsDocumented(CircuitBreakerExit exit, int calls, int failures)
    {
        CircuitBreaker breaker = this.NewExitBreaker();

        await this.EndPermittedCallAsync(breaker, exit).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((calls, failures), breaker.GetCounts());
        Assert.IsEmpty(this.Changes);

        // The call holds nothing, so the next call goes through and is counted.
        await SucceedAsync(this.NewPipeline(breaker), 1).ConfigureAwait(false);
        Assert.AreEqual((calls + 1, failures), breaker.GetCounts());
    }

    [TestMethod]
    [DataRow(CircuitBreakerExit.Success, CircuitState.Closed)]
    [DataRow(CircuitBreakerExit.Failure, CircuitState.Open)]
    [DataRow(CircuitBreakerExit.ResultRejected, CircuitState.Open)]
    [DataRow(CircuitBreakerExit.ResultCheckThrows, CircuitState.HalfOpen)]
    [DataRow(CircuitBreakerExit.AttemptTimeout, CircuitState.Open)]
    [DataRow(CircuitBreakerExit.TotalTimeout, CircuitState.Open)]
    [DataRow(CircuitBreakerExit.PolicyRefuses, CircuitState.Closed)]
    [DataRow(CircuitBreakerExit.PolicyThrows, CircuitState.HalfOpen)]
    [DataRow(CircuitBreakerExit.OperationCancelsItself, CircuitState.Open)]
    [DataRow(CircuitBreakerExit.CallerCancels, CircuitState.HalfOpen)]
    public async Task EachWayThatTheTestCallEnds_DecidesTheStateAsDocumented_AndReleasesTheTestCall(CircuitBreakerExit exit, CircuitState expected)
    {
        CircuitBreaker breaker = this.NewExitBreaker();
        OpenDirectly(breaker);
        this.fake.Advance(BreakDuration);

        Exception? thrown = await this.EndPermittedCallAsync(breaker, exit).ConfigureAwait(false);

        Assert.AreEqual(expected, breaker.State);
        List<CircuitStateChange> expectedChanges = [new(CircuitState.Open, CircuitState.HalfOpen, null)];
        if (expected != CircuitState.HalfOpen)
        {
            // The change has the exception of a failure. A rejected result is a failure with no exception.
            expectedChanges.Add(new(CircuitState.HalfOpen, expected, expected == CircuitState.Open ? thrown : null));
        }

        Assert.AreSequenceEqual(expectedChanges, this.Changes);

        ReliabilityPipeline next = this.NewPipeline(breaker);
        if (expected == CircuitState.Open)
        {
            // A failure of the test call starts a full new break.
            BrokenCircuitException rejected = await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
                () => next.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
            Assert.AreEqual(BreakDuration, rejected.RetryAfter);
            this.fake.Advance(BreakDuration);
        }

        // Nothing holds the test call, so the next call goes through: as the test call if the circuit is not closed.
        await SucceedAsync(next, 1).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task WhenThePolicyOfTheBreakerThrowsDuringTheTestCall_TheCallThrowsThatExceptionAndTheNextCallMakesTheTest()
    {
        var policyFailure = new ArithmeticException("The policy failed");
        CircuitBreaker breaker = this.NewBreaker(o => o with
        {
            Policy = RetryPolicies.FromPredicate(ex => ex is FormatException ? throw policyFailure : true),
        });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);

        ArithmeticException thrown = await Assert.ThrowsExactlyAsync<ArithmeticException>(
            () => pipeline.ExecuteAsync<string>(_ => throw new FormatException("Failure")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(policyFailure, thrown);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IsolateAndCloseCompleteWhenOnStateChangedAwaitsOnASingleThreadedSynchronizationContext(bool close)
    {
        SynchronizationContext? contextInCallback = null;
        CircuitBreaker breaker = this.NewBreaker(o => o with
        {
            OnStateChanged = async change =>
            {
                contextInCallback = SynchronizationContext.Current;
                await Task.Yield();
                await this.Record(change).ConfigureAwait(false);
            },
        });
        if (close)
        {
            breaker.Isolate();
        }

        // The thread of the context waits for the callback, so a continuation that is posted to the context never runs.
        (CircuitState state, bool contextIsCurrent) = await BlockedThreadSynchronizationContext.RunOnBlockedThread(
            () =>
            {
                if (close)
                {
                    breaker.Close();
                }
                else
                {
                    breaker.Isolate();
                }

                return breaker.State;
            }).WaitAsync(WaitLimit).ConfigureAwait(false);

        CircuitState expected = close ? CircuitState.Closed : CircuitState.Isolated;
        Assert.AreEqual(expected, state);
        Assert.IsNull(contextInCallback);
        Assert.IsTrue(contextIsCurrent);
        Assert.AreEqual(new CircuitStateChange(close ? CircuitState.Isolated : CircuitState.Closed, expected, null), this.Changes[^1]);
        Assert.HasCount(close ? 2 : 1, this.Changes);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task WhenOnStateChangedThrowsDuringIsolateOrClose_TheMethodThrowsThatExceptionAndTheStateHasChanged(
        bool close, bool awaitsOnASingleThreadedSynchronizationContext)
    {
        CircuitState target = close ? CircuitState.Closed : CircuitState.Isolated;
        InvalidCastException callbackFailure = new("Callback failed");
        CircuitBreaker breaker = this.NewBreaker(o => o with
        {
            OnStateChanged = change => change.To != target
                ? this.Record(change)
                : awaitsOnASingleThreadedSynchronizationContext
                    ? this.RecordThenThrowAfterYieldAsync(change, callbackFailure)
                    : this.RecordThenThrow(change, callbackFailure),
        });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        if (close)
        {
            breaker.Isolate();
        }

        void Act()
        {
            if (close)
            {
                breaker.Close();
            }
            else
            {
                breaker.Isolate();
            }
        }

        Exception? thrown;
        if (awaitsOnASingleThreadedSynchronizationContext)
        {
            (thrown, bool contextIsCurrent) = await BlockedThreadSynchronizationContext.RunOnBlockedThread(
                () =>
                {
                    try
                    {
                        Act();
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                }).WaitAsync(WaitLimit).ConfigureAwait(false);

            // The context is back after the exception.
            Assert.IsTrue(contextIsCurrent);
        }
        else
        {
            thrown = Assert.ThrowsExactly<InvalidCastException>(Act);
        }

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(target, breaker.State);
        Assert.AreEqual(new CircuitStateChange(close ? CircuitState.Isolated : CircuitState.Closed, target, null), this.Changes[^1]);

        // A later call sees the new state.
        if (close)
        {
            await SucceedAsync(pipeline, 1).ConfigureAwait(false);
            Assert.AreEqual((1, 0), breaker.GetCounts());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<IsolatedCircuitException>(
                () => pipeline.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task TwoPipelinesThatShareOneBreaker_SeeTheSameState()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline first = this.NewPipeline(breaker);
        var second = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string> { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });

        await SucceedAsync(first, 5).ConfigureAwait(false);
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => second.ExecuteAsync(_ => throw new InvalidOperationException("Failure")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
        await Assert.ThrowsExactlyAsync<BrokenCircuitException>(
            () => first.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OneHundredParallelCalls_GiveAnExactCount()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { MinimumThroughput = 1000 });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 100),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) =>
            {
                try
                {
                    await pipeline.ExecuteAsync(_ => i % 2 == 0 ? new ValueTask<int>(i) : throw new InvalidOperationException("Failure")).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual((100, 50), breaker.GetCounts());
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task OneHundredParallelCallsThatOpenTheCircuit_GiveAConsistentStateAndNoDeadlock()
    {
        CircuitBreaker breaker = this.NewBreaker();
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        int invocations = 0;
        int rejections = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 100),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) =>
            {
                try
                {
                    await pipeline.ExecuteAsync<int>(_ =>
                    {
                        Interlocked.Increment(ref invocations);
                        throw new InvalidOperationException("Failure");
                    }).ConfigureAwait(false);
                }
                catch (BrokenCircuitException)
                {
                    Interlocked.Increment(ref rejections);
                }
                catch (InvalidOperationException)
                {
                }
            }).WaitAsync(WaitLimit).ConfigureAwait(false);

        // The circuit opens on the tenth failure. Calls that were permitted before it opened still run.
        Assert.AreEqual(CircuitState.Open, breaker.State);
        Assert.AreEqual(100, invocations + rejections);
        Assert.IsInRange(10, 100, invocations);
        Assert.AreEqual(CircuitState.Open, Assert.ContainsSingle(this.Changes).To);
    }

    [TestMethod]
    public async Task TheStateChangeCallbackRunsOutsideTheLock()
    {
        CircuitBreaker? breaker = null;
        bool readFromAnotherThread = false;
        CircuitBreaker created = this.NewBreaker(o => o with
        {
            OnStateChanged = change =>
            {
                lock (this.changes)
                {
                    this.changes.Add(change);
                }

                if (change.To == CircuitState.Open)
                {
                    // Another thread can take the lock while the callback runs, and the callback can change the state.
                    readFromAnotherThread = Task.Run(() => breaker!.State).Wait(WaitLimit);
                    breaker!.Close();
                }

                return ValueTask.CompletedTask;
            },
        });
        breaker = created;
        ReliabilityPipeline pipeline = this.NewPipeline(created);

        await FailAsync(pipeline, 10).ConfigureAwait(false);

        Assert.IsTrue(readFromAnotherThread);
        Assert.AreEqual(CircuitState.Closed, created.State);
        Assert.AreSequenceEqual(new[] { CircuitState.Open, CircuitState.Closed }, this.Changes.Select(c => c.To).ToArray());
    }

    [TestMethod]
    public async Task AnExceptionFromTheCallbackWhenTheBreakEnds_StopsTheCallAndLetsTheNextCallMakeTheTest()
    {
        InvalidCastException callbackFailure = new("Callback failed");
        CircuitBreaker breaker = this.NewBreaker(o => o with
        {
            OnStateChanged = change => change.To == CircuitState.HalfOpen && !this.Changes.Any(c => c.To == CircuitState.HalfOpen)
                ? this.RecordThenThrow(change, callbackFailure)
                : this.Record(change),
        });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);
        await FailAsync(pipeline, 10).ConfigureAwait(false);
        this.fake.Advance(BreakDuration);
        int invocations = 0;

        InvalidCastException thrown = await Assert.ThrowsExactlyAsync<InvalidCastException>(
            () => pipeline.ExecuteAsync(_ => new ValueTask<int>(Interlocked.Increment(ref invocations))).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, thrown);
        Assert.AreEqual(0, invocations);
        await SucceedAsync(pipeline, 1).ConfigureAwait(false);
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void BrokenCircuitExceptionHasAMessageAndAnOptionalRetryAfter()
    {
        InvalidOperationException inner = new();

        Assert.AreEqual("The circuit is open.", new BrokenCircuitException().Message);
        Assert.IsNull(new BrokenCircuitException().RetryAfter);
        Assert.AreEqual("Message", new BrokenCircuitException("Message").Message);
        Assert.AreSame(inner, new BrokenCircuitException("Message", inner).InnerException);
        Assert.AreEqual(TimeSpan.FromSeconds(3), new BrokenCircuitException("Message", TimeSpan.FromSeconds(3)).RetryAfter);

        // A null second argument is not ambiguous: it is the inner exception.
        BrokenCircuitException withNull = new("Message", null);
        Assert.IsNull(withNull.InnerException);
        Assert.IsNull(withNull.RetryAfter);
        Assert.AreEqual("The circuit is isolated.", new IsolatedCircuitException().Message);
        Assert.AreEqual("Message", new IsolatedCircuitException("Message").Message);
        Assert.AreSame(inner, new IsolatedCircuitException("Message", inner).InnerException);
        Assert.IsInstanceOfType<BrokenCircuitException>(new IsolatedCircuitException());
    }

    [TestMethod]
    [DataRow(nameof(CircuitBreakerOptions.FailureRatio), 0.0)]
    [DataRow(nameof(CircuitBreakerOptions.FailureRatio), -0.1)]
    [DataRow(nameof(CircuitBreakerOptions.FailureRatio), 1.01)]
    [DataRow(nameof(CircuitBreakerOptions.FailureRatio), double.NaN)]
    [DataRow(nameof(CircuitBreakerOptions.MinimumThroughput), 1.0)]
    [DataRow(nameof(CircuitBreakerOptions.MinimumThroughput), -5.0)]
    [DataRow(nameof(CircuitBreakerOptions.SamplingDuration), 499.0)]
    [DataRow(nameof(CircuitBreakerOptions.SamplingDuration), -1.0)]
    [DataRow(nameof(CircuitBreakerOptions.SamplingDuration), 4294967295.0)]
    [DataRow(nameof(CircuitBreakerOptions.BreakDuration), 499.0)]
    [DataRow(nameof(CircuitBreakerOptions.BreakDuration), 0.0)]
    [DataRow(nameof(CircuitBreakerOptions.BreakDuration), 4294967295.0)]
    public void AValueOutOfRange_IsRejectedWithThePropertyName(string property, double value)
    {
        CircuitBreakerOptions options = property switch
        {
            nameof(CircuitBreakerOptions.FailureRatio) => new() { FailureRatio = value },
            nameof(CircuitBreakerOptions.MinimumThroughput) => new() { MinimumThroughput = (int)value },
            nameof(CircuitBreakerOptions.SamplingDuration) => new() { SamplingDuration = TimeSpan.FromMilliseconds(value) },
            _ => new() { BreakDuration = TimeSpan.FromMilliseconds(value) },
        };

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CircuitBreaker(options));

        Assert.AreEqual(property, thrown.ParamName);
    }

    [TestMethod]
    public void TheLimitsOfTheRangesAreAccepted()
    {
        _ = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = 1,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromMilliseconds(500),
            BreakDuration = TimeSpan.FromMilliseconds(500),
        });
        _ = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = double.Epsilon,
            SamplingDuration = RetryDelay.MaxSupported,
            BreakDuration = RetryDelay.MaxSupported,
        });
    }

    [TestMethod]
    public void ANullValue_IsRejectedWithThePropertyName()
    {
        ArgumentNullException options = Assert.ThrowsExactly<ArgumentNullException>(() => new CircuitBreaker(null!));
        ArgumentNullException policy = Assert.ThrowsExactly<ArgumentNullException>(() => new CircuitBreaker(new CircuitBreakerOptions { Policy = null! }));
        ArgumentNullException timeProvider = Assert.ThrowsExactly<ArgumentNullException>(() => new CircuitBreaker(new CircuitBreakerOptions { TimeProvider = null! }));

        Assert.AreEqual("options", options.ParamName);
        Assert.AreEqual(nameof(CircuitBreakerOptions.Policy), policy.ParamName);
        Assert.AreEqual(nameof(CircuitBreakerOptions.TimeProvider), timeProvider.ParamName);
    }

    private static async Task SucceedAsync(ReliabilityPipeline pipeline, int times)
    {
        for (int i = 0; i < times; i++)
        {
            Assert.AreEqual("ok", await pipeline.ExecuteAsync(_ => new ValueTask<string>("ok")).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
        }
    }

    // Returns the exception of the last failure.
    private static async Task<Exception> FailAsync(ReliabilityPipeline pipeline, int times)
    {
        Exception? last = null;
        for (int i = 0; i < times; i++)
        {
            last = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => pipeline.ExecuteAsync<string>(_ => throw new InvalidOperationException("Failure")).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
        }

        return last!;
    }

    private static void OpenDirectly(CircuitBreaker breaker)
    {
        for (int i = 0; i < 10; i++)
        {
            Assert.IsTrue(breaker.TryAcquire(out int probe, out _, out _));
            _ = breaker.OnFailure(new InvalidOperationException(), probe);
        }

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    // A real infinite delay that only the token ends, so that no timer from the fake time provider is involved.
    private static async ValueTask<string> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return "never";
    }

    private static async ValueTask<int> NeverCompletesInt32Async(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private CircuitBreaker NewBreaker(Func<CircuitBreakerOptions, CircuitBreakerOptions>? configure = null)
    {
        CircuitBreakerOptions options = new()
        {
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = BreakDuration,
            OnStateChanged = this.Record,
            TimeProvider = this.fake,
        };

        return new CircuitBreaker(configure?.Invoke(options) ?? options);
    }

    private ReliabilityPipeline NewPipeline(CircuitBreaker breaker)
    {
        return new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker, TimeProvider = this.fake });
    }

    private ReliabilityPipeline NewPipelineWithAttemptTimeout(CircuitBreaker breaker)
    {
        return new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = new DoNotRetry(),
            AttemptTimeout = TimeSpan.FromSeconds(1),
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });
    }

    private ValueTask Record(CircuitStateChange change)
    {
        lock (this.changes)
        {
            this.changes.Add(change);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask RecordThenThrow(CircuitStateChange change, Exception exception)
    {
        this.Record(change);
        throw exception;
    }

    private async ValueTask RecordThenThrowAfterYieldAsync(CircuitStateChange change, Exception exception)
    {
        await Task.Yield();
        await this.Record(change).ConfigureAwait(false);
        throw exception;
    }

    // A breaker whose policy refuses an ArgumentException, throws for a FormatException, and accepts other exceptions.
    private CircuitBreaker NewExitBreaker()
    {
        return this.NewBreaker(o => o with
        {
            Policy = RetryPolicies.FromPredicate(ex => ex switch
            {
                FormatException => throw this.exitPolicyFailure,
                ArgumentException => false,
                _ => true,
            }),
        });
    }

    // Makes one call that the breaker permits, and ends it in the way that the exit names. Returns the exception that
    // the caller gets, or null when the caller gets a result.
    private async Task<Exception?> EndPermittedCallAsync(CircuitBreaker breaker, CircuitBreakerExit exit)
    {
        const int ResultThatTheCheckThrowsFor = -2;
        var pipeline = new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int>
        {
            Strategy = new DoNotRetry(),
            ShouldRetryResult = r => r == ResultThatTheCheckThrowsFor ? throw this.resultCheckFailure : r < 0,
            AttemptTimeout = exit == CircuitBreakerExit.AttemptTimeout ? ExitTimeout : null,
            TotalTimeout = exit == CircuitBreakerExit.TotalTimeout ? ExitTimeout : null,
            CircuitBreaker = breaker,
            TimeProvider = this.fake,
        });

        // The caller's token is live, so that a token of the operation is never the caller's.
        using CancellationTokenSource caller = new();
        switch (exit)
        {
            case CircuitBreakerExit.Success:
            case CircuitBreakerExit.ResultRejected:
                int result = exit == CircuitBreakerExit.Success ? 1 : -1;
                Assert.AreEqual(result, await pipeline.ExecuteAsync(_ => new ValueTask<int>(result), caller.Token).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false));
                return null;

            case CircuitBreakerExit.ResultCheckThrows:
                InvalidCastException checkFailure = await Assert.ThrowsExactlyAsync<InvalidCastException>(
                    () => pipeline.ExecuteAsync(_ => new ValueTask<int>(ResultThatTheCheckThrowsFor), caller.Token).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.AreSame(this.resultCheckFailure, checkFailure);
                return checkFailure;

            case CircuitBreakerExit.AttemptTimeout:
            case CircuitBreakerExit.TotalTimeout:
                Task<int> timedCall = pipeline.ExecuteAsync(NeverCompletesInt32Async, caller.Token).AsTask();
                this.fake.Advance(ExitTimeout);
                RetryTimeoutException timeout = await Assert.ThrowsExactlyAsync<RetryTimeoutException>(() => timedCall.WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.AreEqual(ExitTimeout, timeout.Timeout);
                return timeout;

            case CircuitBreakerExit.CallerCancels:
                Task<int> cancelledCall = pipeline.ExecuteAsync(NeverCompletesInt32Async, caller.Token).AsTask();
                await caller.CancelAsync().ConfigureAwait(false);
                TaskCanceledException cancelled = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelledCall.WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.AreEqual(caller.Token, cancelled.CancellationToken);
                return cancelled;

            default:
                Exception failure = exit switch
                {
                    CircuitBreakerExit.Failure => new InvalidOperationException("Failure"),
                    CircuitBreakerExit.PolicyRefuses => new ArgumentException("A failure that the policy refuses"),
                    CircuitBreakerExit.PolicyThrows => new FormatException("A failure that the policy throws for"),
                    _ => new TaskCanceledException("The operation cancelled itself", null, new CancellationToken(canceled: true)),
                };
                Exception thrown = await Assert.ThrowsAsync<Exception>(
                    () => pipeline.ExecuteAsync(_ => throw failure, caller.Token).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);
                Assert.AreSame(exit == CircuitBreakerExit.PolicyThrows ? this.exitPolicyFailure : failure, thrown);
                return thrown;
        }
    }

    /// <summary>
    /// A time provider whose wall clock and monotonic clock move separately. <see cref="FakeTimeProvider"/> cannot
    /// move its wall clock back.
    /// </summary>
    private sealed class SplitClockTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => this.utcNow;

        public override long GetTimestamp() => this.timestamp;

        public void MoveWallClock(TimeSpan time) => this.utcNow += time;

        public void MoveMonotonicClock(TimeSpan time) => this.timestamp += time.Ticks;
    }
}
