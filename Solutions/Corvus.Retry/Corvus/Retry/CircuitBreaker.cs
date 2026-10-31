// <copyright file="CircuitBreaker.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// Stops calls to a service that fails frequently.
/// </summary>
/// <remarks>
/// <para>
/// One instance holds the state of the circuit. Many pipelines and many threads can use the same instance: set it as
/// the <see cref="ReliabilityPipelineOptions.CircuitBreaker"/> of each pipeline. The pipeline asks the breaker for permission
/// before each attempt, so each retry is one call.
/// </para>
/// <para>
/// When the circuit is closed, the breaker counts the calls and the failures in a sliding window of
/// <see cref="SamplingWindow"/>, with 10 buckets. When the window has <see cref="MinimumCalls"/> calls or more, and the
/// ratio of failures is <see cref="FailureRate"/> or more, the circuit opens. An open circuit rejects each call with a
/// <see cref="CircuitRejectedException"/>. After <see cref="OpenDuration"/>, the circuit is
/// half-open: the breaker lets one test call through, and rejects the other calls. If the test call succeeds, the
/// circuit closes and the counts start again. If it fails, the circuit opens again. The outcome of a test call that
/// started before <see cref="Isolate"/>, <see cref="Close"/> or a later break does not change the state. If the circuit
/// is closed when such a test call ends, the breaker does not count the call either: it started before the counts
/// started again.
/// </para>
/// <para>
/// A failure is an exception that <see cref="Policy"/> accepts, a <see cref="RetryTimeoutException"/>,
/// or a result that <see cref="ReliabilityPipelineOptions{T}.ShouldRetryResult"/> rejects. A cancellation by the caller is not
/// a call and not a failure. A call that the breaker rejects is not a call.
/// </para>
/// <para>
/// The breaker changes its state under a lock, and calls <see cref="OnStateChanged"/> after it
/// releases the lock.
/// </para>
/// </remarks>
public sealed class CircuitBreaker
{
    private const int BucketCount = 10;

    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultSamplingWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(5);

    private readonly Lock sync = new();
    private readonly double failureRate;
    private readonly int minimumCalls;
    private readonly TimeSpan samplingWindow;
    private readonly TimeSpan openDuration;
    private readonly long bucketTicks;
    private readonly IRetryPolicy policy;
    private readonly Func<CircuitStateChange, ValueTask>? onStateChanged;
    private readonly TimeProvider timeProvider;

    // All times are measured from this timestamp of the monotonic clock, so a change of the wall clock has no effect.
    private readonly long originTimestamp;

    // The sliding window is a ring of buckets. The head bucket is the newest, and started at headBucketStart
    // (a time since originTimestamp).
    private readonly int[] calls = new int[BucketCount];
    private readonly int[] failures = new int[BucketCount];
    private int head;
    private TimeSpan headBucketStart;
    private int windowCalls;
    private int windowFailures;

    // Changes are made under the lock. The field is volatile, so that TryAcquire can read it without the lock.
    private volatile CircuitState state;
    private TimeSpan breakUntil;

    // The number of the test call in flight, or 0 if there is none. Each test call gets a new number, so that a test
    // call from before Isolate, Close or a new break cannot change the state or release a later test call.
    private int probeInFlight;
    private int lastProbe;

    /// <summary>
    /// Initialises a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="failureRate">
    /// The ratio of failures to calls at which the circuit opens. The default is 0.1. The value must be greater than 0
    /// and not greater than 1. The circuit opens when the ratio in the sampling window is equal to this value or greater.
    /// </param>
    /// <param name="minimumCalls">
    /// The smallest number of calls in the sampling window before the circuit can open. The default is 100. The value
    /// must be 2 or more.
    /// </param>
    /// <param name="samplingWindow">
    /// The length of the sliding window in which the breaker counts the calls and the failures, or
    /// <see langword="null"/> for the default of 30 seconds. The value must be 500 milliseconds or more, and not greater
    /// than about 49.7 days. The breaker divides the window into 10 buckets, and drops a bucket when it becomes older
    /// than the window.
    /// </param>
    /// <param name="openDuration">
    /// The time for which the circuit stays open before it lets a test call through, or <see langword="null"/> for the
    /// default of 5 seconds. The value must be 500 milliseconds or more, and not greater than about 49.7 days.
    /// </param>
    /// <param name="policy">
    /// The policy that selects the exceptions that are failures, or <see langword="null"/> for
    /// <see cref="AnyExceptionPolicy.Default"/>. See <see cref="Policy"/>.
    /// </param>
    /// <param name="onStateChanged">
    /// A callback that the breaker calls after each change of its state, or <see langword="null"/>. See
    /// <see cref="OnStateChanged"/>.
    /// </param>
    /// <param name="timeProvider">
    /// The time provider that gives the current time for the sampling window and the open duration, or
    /// <see langword="null"/> for <see cref="TimeProvider.System"/>. A test can use a fake time provider.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="failureRate"/> is not greater than 0 or is greater than 1, <paramref name="minimumCalls"/> is less
    /// than 2, or <paramref name="samplingWindow"/> or <paramref name="openDuration"/> is less than 500 milliseconds or
    /// greater than the longest time that a timer supports.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the parameter that is not correct.
    /// </para>
    /// <para>
    /// The settings are constructor parameters, not properties that an object initialiser sets, because the breaker
    /// reads the clock of <paramref name="timeProvider"/> when it is constructed.
    /// </para>
    /// </remarks>
    public CircuitBreaker(
        double failureRate = 0.1,
        int minimumCalls = 100,
        TimeSpan? samplingWindow = null,
        TimeSpan? openDuration = null,
        IRetryPolicy? policy = null,
        Func<CircuitStateChange, ValueTask>? onStateChanged = null,
        TimeProvider? timeProvider = null)
    {
        TimeSpan window = samplingWindow ?? DefaultSamplingWindow;
        TimeSpan open = openDuration ?? DefaultOpenDuration;

        // This also rejects NaN.
        if (!(failureRate > 0 && failureRate <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureRate),
                failureRate,
                "The failure rate must be greater than 0 and not greater than 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCalls, 2, nameof(minimumCalls));
        ThrowIfOutOfRange(window, nameof(samplingWindow));
        ThrowIfOutOfRange(open, nameof(openDuration));

        this.failureRate = failureRate;
        this.minimumCalls = minimumCalls;
        this.samplingWindow = window;
        this.openDuration = open;
        this.bucketTicks = window.Ticks / BucketCount;
        this.policy = policy ?? AnyExceptionPolicy.Default;
        this.onStateChanged = onStateChanged;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.originTimestamp = this.timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the ratio of failures to calls at which the circuit opens.
    /// </summary>
    /// <remarks>
    /// The circuit opens when the ratio in the sampling window is equal to this value or greater, and the window has
    /// <see cref="MinimumCalls"/> calls or more.
    /// </remarks>
    public double FailureRate => this.failureRate;

    /// <summary>
    /// Gets the smallest number of calls in the sampling window before the circuit can open.
    /// </summary>
    public int MinimumCalls => this.minimumCalls;

    /// <summary>
    /// Gets the length of the sliding window in which the breaker counts the calls and the failures.
    /// </summary>
    public TimeSpan SamplingWindow => this.samplingWindow;

    /// <summary>
    /// Gets the time for which the circuit stays open before it lets a test call through.
    /// </summary>
    public TimeSpan OpenDuration => this.openDuration;

    /// <summary>
    /// Gets the policy that selects the exceptions that are failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A call that throws an exception that the policy refuses counts as a call that succeeded.
    /// </para>
    /// <para>
    /// When a call throws an <see cref="AggregateException"/>, the breaker asks the policy about the exceptions inside
    /// it, not about the wrapper, in depth-first order. The call is a failure only if the policy accepts each of them.
    /// An <see cref="AggregateException"/> with no inner exceptions is itself the exception that the policy gets.
    /// </para>
    /// <para>
    /// A <see cref="RetryTimeoutException"/> is always a failure. The breaker does not ask the policy about it. If the
    /// policy throws, the call throws that exception, and the call is not counted.
    /// </para>
    /// </remarks>
    public IRetryPolicy Policy => this.policy;

    /// <summary>
    /// Gets the callback that the breaker calls after each change of its state, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The breaker calls the callback after it releases its lock, so the callback can read <see cref="State"/>, and can
    /// call <see cref="Isolate"/> or <see cref="Close"/>.
    /// </para>
    /// <para>
    /// A factory awaits the callback, and an exception from the callback stops the call. The <see cref="Isolate"/> and
    /// <see cref="Close"/> methods block until the callback completes, so the callback must be quick.
    /// </para>
    /// <para>
    /// When <see cref="Isolate"/> or <see cref="Close"/> calls it, the callback runs with no
    /// <see cref="SynchronizationContext"/>, and with <see cref="TaskScheduler.Default"/> as the current scheduler. If it
    /// awaits, the continuation runs on the thread pool. Do not use a callback that must continue on a custom
    /// <see cref="TaskScheduler"/>.
    /// </para>
    /// </remarks>
    public Func<CircuitStateChange, ValueTask>? OnStateChanged => this.onStateChanged;

    /// <summary>
    /// Gets the time provider that gives the current time for the sampling window and the open duration.
    /// </summary>
    public TimeProvider TimeProvider => this.timeProvider;

    /// <summary>
    /// Gets the state of the circuit.
    /// </summary>
    /// <remarks>
    /// The circuit goes from <see cref="CircuitState.Open"/> to <see cref="CircuitState.HalfOpen"/> on the first call
    /// after the break ends, not when the break ends. Until that call, this property gives
    /// <see cref="CircuitState.Open"/>.
    /// </remarks>
    public CircuitState State => this.state;

    /// <summary>
    /// Isolates the circuit: the breaker rejects each call with a <see cref="CircuitIsolatedException"/> until
    /// <see cref="Close"/> is called.
    /// </summary>
    /// <remarks>
    /// If the circuit is not isolated already, this calls <see cref="OnStateChanged"/>, and waits
    /// until the callback completes. The callback runs with no <see cref="SynchronizationContext"/>, and with
    /// <see cref="TaskScheduler.Default"/> as the current scheduler.
    /// </remarks>
    public void Isolate()
    {
        CircuitStateChange? change = null;
        lock (this.sync)
        {
            if (this.state != CircuitState.Isolated)
            {
                change = new CircuitStateChange(this.state, CircuitState.Isolated, null);
                this.state = CircuitState.Isolated;
                this.probeInFlight = 0;
            }
        }

        this.Notify(change);
    }

    /// <summary>
    /// Closes the circuit, and clears the counts of calls and failures.
    /// </summary>
    /// <remarks>
    /// If the circuit is not closed already, this calls <see cref="OnStateChanged"/>, and waits
    /// until the callback completes. The callback runs with no <see cref="SynchronizationContext"/>, and with
    /// <see cref="TaskScheduler.Default"/> as the current scheduler.
    /// </remarks>
    public void Close()
    {
        TimeSpan now = this.Now();
        CircuitStateChange? change = null;
        lock (this.sync)
        {
            if (this.state != CircuitState.Closed)
            {
                change = new CircuitStateChange(this.state, CircuitState.Closed, null);
                this.state = CircuitState.Closed;
                this.probeInFlight = 0;
            }

            this.ResetWindow(now);
        }

        this.Notify(change);
    }

    /// <summary>
    /// Asks for permission to make a call.
    /// </summary>
    /// <param name="probe">
    /// When this method returns <see langword="true"/>, the number of the test call of a half-open circuit, or 0 if the
    /// call is not the test call. The caller passes it to <see cref="OnSuccess"/>, <see cref="OnFailure"/> or
    /// <see cref="Abandon"/>.
    /// </param>
    /// <param name="rejection">
    /// When this method returns <see langword="false"/>, the exception to throw for the rejected call.
    /// </param>
    /// <param name="change">The change of state that the request caused, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the call can go through; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This does not count the call. The caller reports the outcome of a permitted call with <see cref="OnSuccess"/>,
    /// <see cref="OnFailure"/> or <see cref="Abandon"/>, and passes <paramref name="change"/> to
    /// <see cref="NotifyAsync"/>.
    /// </remarks>
    internal bool TryAcquire(out int probe, out CircuitRejectedException? rejection, out CircuitStateChange? change)
    {
        probe = 0;
        rejection = null;
        change = null;

        // A closed circuit needs no time and no lock, so the usual case does not read the clock. The state can change
        // after this read, as it can after the lock is released, so the result is the same as a read under the lock.
        if (this.state == CircuitState.Closed)
        {
            return true;
        }

        // The time provider is the caller's code, so do not run it under the lock.
        TimeSpan now = this.Now();
        TimeSpan retryAfter = TimeSpan.Zero;
        CircuitState rejectedState;
        lock (this.sync)
        {
            switch (this.state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open when now >= this.breakUntil:
                    // The break has ended. This call is the test call.
                    this.state = CircuitState.HalfOpen;
                    probe = this.StartProbe();
                    change = new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null);
                    return true;

                case CircuitState.Open:
                    retryAfter = this.breakUntil - now;
                    break;

                case CircuitState.HalfOpen when this.probeInFlight == 0:
                    probe = this.StartProbe();
                    return true;
            }

            rejectedState = this.state;
        }

        // Make the exception after the lock is released.
        rejection = rejectedState switch
        {
            CircuitState.Isolated => new CircuitIsolatedException(),
            CircuitState.HalfOpen => new CircuitRejectedException("The circuit is half-open, and a test call is in progress."),
            _ => new CircuitRejectedException("The circuit is open.", retryAfter),
        };

        return false;
    }

    /// <summary>
    /// Reports that a permitted call succeeded.
    /// </summary>
    /// <param name="probe">The number of the test call that <see cref="TryAcquire"/> gave, or 0.</param>
    /// <returns>The change of state that the success caused, or <see langword="null"/>.</returns>
    internal CircuitStateChange? OnSuccess(int probe)
    {
        TimeSpan now = this.Now();
        lock (this.sync)
        {
            if (this.state == CircuitState.Closed)
            {
                if (IsCallOfTheClosedWindow(probe))
                {
                    this.Slide(now);
                    this.Record(failed: false);
                }
            }
            else if (this.IsProbeInFlight(probe))
            {
                this.state = CircuitState.Closed;
                this.probeInFlight = 0;
                this.ResetWindow(now);
                return new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Closed, null);
            }

            // A call that started before the circuit opened or was isolated, or a test call from before that, does not
            // change the state.
            return null;
        }
    }

    /// <summary>
    /// Reports that a permitted call failed.
    /// </summary>
    /// <param name="exception">
    /// The exception of the call, or <see langword="null"/> when the call returned a result that is a failure.
    /// </param>
    /// <param name="probe">The number of the test call that <see cref="TryAcquire"/> gave, or 0.</param>
    /// <returns>The change of state that the failure caused, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// A <see cref="RetryTimeoutException"/> is always a failure, and the breaker does not ask
    /// <see cref="Policy"/> about it. Another exception that the policy refuses is not a failure, so
    /// the call counts as a success. For an <see cref="AggregateException"/>, the breaker asks the policy about each
    /// exception inside it, and the call is a failure only if the policy accepts all of them. The
    /// <see cref="CircuitStateChange.LastException"/> of a change is the exception as the call threw it.
    /// </para>
    /// <para>
    /// If the policy throws, this method throws that exception, and does not count the call. The caller must then call
    /// <see cref="Abandon"/>, so that the next call can make the test call. This method does not call it, so that the
    /// caller calls it once for each call.
    /// </para>
    /// </remarks>
    internal CircuitStateChange? OnFailure(Exception? exception, int probe)
    {
        // The policy is the caller's code, so do not run it under the lock. The engine raises a RetryTimeoutException
        // when an attempt times out, which is always a failure. The policy is asked about the exceptions inside an
        // AggregateException, not about the wrapper.
        if (exception is not null && exception is not RetryTimeoutException && !this.IsFailure(exception))
        {
            return this.OnSuccess(probe);
        }

        TimeSpan now = this.Now();
        lock (this.sync)
        {
            if (this.state == CircuitState.Closed)
            {
                if (IsCallOfTheClosedWindow(probe))
                {
                    this.Slide(now);
                    this.Record(failed: true);
                    if (this.windowCalls >= this.minimumCalls && this.windowFailures / (double)this.windowCalls >= this.failureRate)
                    {
                        this.Open(now);
                        return new CircuitStateChange(CircuitState.Closed, CircuitState.Open, exception);
                    }
                }
            }
            else if (this.IsProbeInFlight(probe))
            {
                this.Open(now);
                return new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Open, exception);
            }

            return null;
        }
    }

    /// <summary>
    /// Reports that a permitted call ended without an outcome (for example, because the caller cancelled it).
    /// </summary>
    /// <param name="probe">The number of the test call that <see cref="TryAcquire"/> gave, or 0.</param>
    /// <remarks>
    /// The call is not counted. If it is the test call in flight, the next call becomes the test call. Call this method
    /// once for each call.
    /// </remarks>
    internal void Abandon(int probe)
    {
        lock (this.sync)
        {
            if (this.IsProbeInFlight(probe))
            {
                this.probeInFlight = 0;
            }
        }
    }

    /// <summary>
    /// Calls <see cref="OnStateChanged"/> for a change of state, if there is a change and a
    /// callback.
    /// </summary>
    /// <param name="change">The change of state, or <see langword="null"/>.</param>
    /// <returns>The task of the callback, or a completed task.</returns>
    /// <remarks>Call this method only after the lock is released.</remarks>
    internal ValueTask NotifyAsync(CircuitStateChange? change)
    {
        return change is CircuitStateChange c && this.onStateChanged is { } callback ? callback(c) : default;
    }

    /// <summary>
    /// Gets the number of calls and failures in the sampling window.
    /// </summary>
    /// <returns>The number of calls and the number of failures.</returns>
    internal (int Calls, int Failures) GetCounts()
    {
        TimeSpan now = this.Now();
        lock (this.sync)
        {
            this.Slide(now);
            return (this.windowCalls, this.windowFailures);
        }
    }

    // Gets a value that indicates whether a call that ends while the circuit is closed counts in the sampling window.
    // Only a half-open circuit gives a call a test call number, and a test call that closes the circuit ends as it
    // closes it. So a test call that ends while the circuit is closed is from a generation that Close ended, and it
    // started before the counts started again.
    private static bool IsCallOfTheClosedWindow(int probe) => probe == 0;

    private static void ThrowIfOutOfRange(TimeSpan value, string propertyName)
    {
        if (value < MinimumDuration || value > RetryDelay.MaxSupported)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                $"The time must be at least {MinimumDuration} and not greater than {RetryDelay.MaxSupported}.");
        }
    }

    private bool IsFailure(Exception exception)
    {
        return exception is AggregateException aggregate
            ? this.policy.CanRetryAll(aggregate)
            : this.policy.CanRetry(exception);
    }

    private void Notify(CircuitStateChange? change)
    {
        // The callback is documented to be quick, so this method can wait for it.
        if (change is CircuitStateChange c && this.onStateChanged is { } callback)
        {
            SynchronousCallback.InvokeAndWait(callback, c);
        }
    }

    private void Open(TimeSpan now)
    {
        this.state = CircuitState.Open;
        this.breakUntil = now + this.openDuration;
        this.probeInFlight = 0;
    }

    // Call this method under the lock.
    private int StartProbe()
    {
        // The number is never 0, which means "not a test call". It can wrap, but a test call that is 2^32 test calls old
        // is not in flight.
        int probe = unchecked(this.lastProbe + 1);
        this.lastProbe = probe == 0 ? 1 : probe;
        this.probeInFlight = this.lastProbe;
        return this.lastProbe;
    }

    // Call this method under the lock.
    private bool IsProbeInFlight(int probe)
    {
        return probe != 0 && probe == this.probeInFlight && this.state == CircuitState.HalfOpen;
    }

    private void Record(bool failed)
    {
        this.calls[this.head]++;
        this.windowCalls++;
        if (failed)
        {
            this.failures[this.head]++;
            this.windowFailures++;
        }
    }

    // Drops the buckets that are older than the window.
    private void Slide(TimeSpan now)
    {
        long elapsedBuckets = (now - this.headBucketStart).Ticks / this.bucketTicks;
        if (elapsedBuckets <= 0)
        {
            return;
        }

        if (elapsedBuckets >= BucketCount)
        {
            this.ResetWindow(now);
            return;
        }

        for (long i = 0; i < elapsedBuckets; i++)
        {
            this.head = (this.head + 1) % BucketCount;
            this.windowCalls -= this.calls[this.head];
            this.windowFailures -= this.failures[this.head];
            this.calls[this.head] = 0;
            this.failures[this.head] = 0;
        }

        this.headBucketStart += TimeSpan.FromTicks(elapsedBuckets * this.bucketTicks);
    }

    // Gets the time since originTimestamp from the monotonic clock of the time provider.
    private TimeSpan Now() => this.timeProvider.GetElapsedTime(this.originTimestamp);

    private void ResetWindow(TimeSpan now)
    {
        Array.Clear(this.calls);
        Array.Clear(this.failures);
        this.head = 0;
        this.headBucketStart = now;
        this.windowCalls = 0;
        this.windowFailures = 0;
    }
}
