// <copyright file="CircuitBreakerTests.cs" company="Endjin Limited">
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

    private readonly FakeTimeProvider fake = new();
    private readonly List<CircuitStateChange> changes = [];

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
        Assert.IsTrue(breaker.TryAcquire(out bool probe, out _, out CircuitStateChange? change));
        Assert.IsTrue(probe);
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
    public async Task AnExceptionThatThePolicyRefuses_IsNotAFailure()
    {
        CircuitBreaker breaker = this.NewBreaker(o => o with { Policy = RetryPolicies.Handle<TimeoutException>() });
        ReliabilityPipeline pipeline = this.NewPipeline(breaker);

        await FailAsync(pipeline, 10).ConfigureAwait(false);

        Assert.AreEqual(CircuitState.Closed, breaker.State);
        Assert.AreEqual((10, 0), breaker.GetCounts());
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
            Assert.IsTrue(breaker.TryAcquire(out bool probe, out _, out _));
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
