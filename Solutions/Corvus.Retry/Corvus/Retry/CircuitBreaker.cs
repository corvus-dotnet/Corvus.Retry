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
/// the <see cref="RetryPipelineOptions.CircuitBreaker"/> of each pipeline. The pipeline asks the breaker for permission
/// before each attempt, so each retry is one call.
/// </para>
/// <para>
/// When the circuit is closed, the breaker counts the calls and the failures in a sliding window of
/// <see cref="CircuitBreakerOptions.SamplingDuration"/>, with 10 buckets. When the window has
/// <see cref="CircuitBreakerOptions.MinimumThroughput"/> calls or more, and the ratio of failures is
/// <see cref="CircuitBreakerOptions.FailureRatio"/> or more, the circuit opens. An open circuit rejects each call with a
/// <see cref="BrokenCircuitException"/>. After <see cref="CircuitBreakerOptions.BreakDuration"/>, the circuit is
/// half-open: the breaker lets one test call through, and rejects the other calls. If the test call succeeds, the
/// circuit closes and the counts start again. If it fails, the circuit opens again.
/// </para>
/// <para>
/// A failure is an exception that <see cref="CircuitBreakerOptions.Policy"/> accepts, or a result that
/// <see cref="RetryPipelineOptions{T}.ShouldRetryResult"/> rejects. A cancellation by the caller is not a call and not
/// a failure. A call that the breaker rejects is not a call.
/// </para>
/// <para>
/// The breaker changes its state under a lock, and calls <see cref="CircuitBreakerOptions.OnStateChanged"/> after it
/// releases the lock.
/// </para>
/// </remarks>
public sealed class CircuitBreaker
{
    private const int BucketCount = 10;

    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(500);

    private readonly Lock sync = new();
    private readonly double failureRatio;
    private readonly int minimumThroughput;
    private readonly TimeSpan breakDuration;
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
    private bool probeInFlight;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="options">The settings of the breaker.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <see cref="CircuitBreakerOptions.Policy"/> or
    /// <see cref="CircuitBreakerOptions.TimeProvider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="CircuitBreakerOptions.FailureRatio"/> is not greater than 0 or is greater than 1,
    /// <see cref="CircuitBreakerOptions.MinimumThroughput"/> is less than 2, or
    /// <see cref="CircuitBreakerOptions.SamplingDuration"/> or <see cref="CircuitBreakerOptions.BreakDuration"/> is less
    /// than 500 milliseconds or greater than the longest time that a timer supports.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the property that is not correct.
    /// </remarks>
    public CircuitBreaker(CircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // This also rejects NaN.
        if (!(options.FailureRatio > 0 && options.FailureRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CircuitBreakerOptions.FailureRatio),
                options.FailureRatio,
                "The failure ratio must be greater than 0 and not greater than 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumThroughput, 2, nameof(CircuitBreakerOptions.MinimumThroughput));
        ThrowIfOutOfRange(options.SamplingDuration, nameof(CircuitBreakerOptions.SamplingDuration));
        ThrowIfOutOfRange(options.BreakDuration, nameof(CircuitBreakerOptions.BreakDuration));
        ArgumentNullException.ThrowIfNull(options.Policy, nameof(CircuitBreakerOptions.Policy));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(CircuitBreakerOptions.TimeProvider));

        this.failureRatio = options.FailureRatio;
        this.minimumThroughput = options.MinimumThroughput;
        this.breakDuration = options.BreakDuration;
        this.bucketTicks = options.SamplingDuration.Ticks / BucketCount;
        this.policy = options.Policy;
        this.onStateChanged = options.OnStateChanged;
        this.timeProvider = options.TimeProvider;
        this.originTimestamp = this.timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the state of the circuit.
    /// </summary>
    /// <remarks>
    /// The circuit goes from <see cref="CircuitState.Open"/> to <see cref="CircuitState.HalfOpen"/> on the first call
    /// after the break ends, not when the break ends. Until that call, this property gives
    /// <see cref="CircuitState.Open"/>.
    /// </remarks>
    public CircuitState State
    {
        get
        {
            lock (this.sync)
            {
                return this.state;
            }
        }
    }

    /// <summary>
    /// Isolates the circuit: the breaker rejects each call with an <see cref="IsolatedCircuitException"/> until
    /// <see cref="Close"/> is called.
    /// </summary>
    /// <remarks>
    /// If the circuit is not isolated already, this calls <see cref="CircuitBreakerOptions.OnStateChanged"/>, and waits
    /// until the callback completes.
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
                this.probeInFlight = false;
            }
        }

        this.Notify(change);
    }

    /// <summary>
    /// Closes the circuit, and clears the counts of calls and failures.
    /// </summary>
    /// <remarks>
    /// If the circuit is not closed already, this calls <see cref="CircuitBreakerOptions.OnStateChanged"/>, and waits
    /// until the callback completes.
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
                this.probeInFlight = false;
            }

            this.ResetWindow(now);
        }

        this.Notify(change);
    }

    /// <summary>
    /// Asks for permission to make a call.
    /// </summary>
    /// <param name="probe">
    /// When this method returns <see langword="true"/>, indicates whether the call is the test call of a half-open
    /// circuit.
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
    internal bool TryAcquire(out bool probe, out BrokenCircuitException? rejection, out CircuitStateChange? change)
    {
        probe = false;
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
                    this.probeInFlight = true;
                    probe = true;
                    change = new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, null);
                    return true;

                case CircuitState.Open:
                    retryAfter = this.breakUntil - now;
                    break;

                case CircuitState.HalfOpen when !this.probeInFlight:
                    this.probeInFlight = true;
                    probe = true;
                    return true;
            }

            rejectedState = this.state;
        }

        // Make the exception after the lock is released.
        rejection = rejectedState switch
        {
            CircuitState.Isolated => new IsolatedCircuitException(),
            CircuitState.HalfOpen => new BrokenCircuitException("The circuit is half-open, and a test call is in progress.", (TimeSpan?)null),
            _ => new BrokenCircuitException("The circuit is open.", retryAfter),
        };

        return false;
    }

    /// <summary>
    /// Reports that a permitted call succeeded.
    /// </summary>
    /// <param name="probe">Indicates whether the call was the test call of a half-open circuit.</param>
    /// <returns>The change of state that the success caused, or <see langword="null"/>.</returns>
    internal CircuitStateChange? OnSuccess(bool probe)
    {
        TimeSpan now = this.Now();
        lock (this.sync)
        {
            if (this.state == CircuitState.Closed)
            {
                this.Slide(now);
                this.Record(failed: false);
            }
            else if (this.state == CircuitState.HalfOpen && probe)
            {
                this.state = CircuitState.Closed;
                this.probeInFlight = false;
                this.ResetWindow(now);
                return new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Closed, null);
            }

            // A call that started before the circuit opened, or that was isolated, does not change the state.
            return null;
        }
    }

    /// <summary>
    /// Reports that a permitted call failed.
    /// </summary>
    /// <param name="exception">
    /// The exception of the call, or <see langword="null"/> when the call returned a result that is a failure.
    /// </param>
    /// <param name="probe">Indicates whether the call was the test call of a half-open circuit.</param>
    /// <returns>The change of state that the failure caused, or <see langword="null"/>.</returns>
    /// <remarks>
    /// An exception that <see cref="CircuitBreakerOptions.Policy"/> refuses is not a failure, so the call counts as a
    /// success.
    /// </remarks>
    internal CircuitStateChange? OnFailure(Exception? exception, bool probe)
    {
        // The policy is the caller's code, so do not run it under the lock.
        if (exception is not null && !this.policy.CanRetry(exception))
        {
            return this.OnSuccess(probe);
        }

        TimeSpan now = this.Now();
        lock (this.sync)
        {
            if (this.state == CircuitState.Closed)
            {
                this.Slide(now);
                this.Record(failed: true);
                if (this.windowCalls >= this.minimumThroughput && this.windowFailures / (double)this.windowCalls >= this.failureRatio)
                {
                    this.Open(now);
                    return new CircuitStateChange(CircuitState.Closed, CircuitState.Open, exception);
                }
            }
            else if (this.state == CircuitState.HalfOpen && probe)
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
    /// <param name="probe">Indicates whether the call was the test call of a half-open circuit.</param>
    /// <remarks>The call is not counted. If it was the test call, the next call becomes the test call.</remarks>
    internal void Abandon(bool probe)
    {
        lock (this.sync)
        {
            if (this.state == CircuitState.HalfOpen && probe)
            {
                this.probeInFlight = false;
            }
        }
    }

    /// <summary>
    /// Calls <see cref="CircuitBreakerOptions.OnStateChanged"/> for a change of state, if there is a change and a
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

    private void Notify(CircuitStateChange? change)
    {
        // The callback is documented to be quick, so this method can wait for it.
        ValueTask callback = this.NotifyAsync(change);
        if (callback.IsCompletedSuccessfully)
        {
            callback.GetAwaiter().GetResult();
        }
        else
        {
            callback.AsTask().GetAwaiter().GetResult();
        }
    }

    private void Open(TimeSpan now)
    {
        this.state = CircuitState.Open;
        this.breakUntil = now + this.breakDuration;
        this.probeInFlight = false;
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
