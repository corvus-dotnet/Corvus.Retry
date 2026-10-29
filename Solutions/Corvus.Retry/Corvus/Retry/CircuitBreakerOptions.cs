// <copyright file="CircuitBreakerOptions.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The settings of a <see cref="CircuitBreaker"/>.
/// </summary>
/// <remarks>
/// The options are immutable. Use a <see langword="with"/> expression to make a changed copy. The constructor of the
/// breaker checks the options, and keeps their values, so a breaker does not read the options again.
/// </remarks>
public sealed record CircuitBreakerOptions
{
    /// <summary>
    /// Gets the ratio of failures to calls at which the circuit opens.
    /// </summary>
    /// <remarks>
    /// The default is 0.1. The value must be greater than 0 and not greater than 1. The circuit opens when the ratio in
    /// the sampling window is equal to this value or greater.
    /// </remarks>
    public double FailureRatio { get; init; } = 0.1;

    /// <summary>
    /// Gets the smallest number of calls in the sampling window before the circuit can open.
    /// </summary>
    /// <remarks>The default is 100. The value must be 2 or more.</remarks>
    public int MinimumThroughput { get; init; } = 100;

    /// <summary>
    /// Gets the length of the sliding window in which the breaker counts the calls and the failures.
    /// </summary>
    /// <remarks>
    /// The default is 30 seconds. The value must be 500 milliseconds or more, and not greater than about 49.7 days. The
    /// breaker divides the window into 10 buckets, and drops a bucket when it becomes older than the window.
    /// </remarks>
    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the time for which the circuit stays open before it lets a test call through.
    /// </summary>
    /// <remarks>
    /// The default is 5 seconds. The value must be 500 milliseconds or more, and not greater than about 49.7 days.
    /// </remarks>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the policy that selects the exceptions that are failures.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. A call that throws an exception that the policy refuses
    /// counts as a call that succeeded.
    /// </remarks>
    public IRetryPolicy Policy { get; init; } = AnyExceptionPolicy.Default;

    /// <summary>
    /// Gets a callback that the breaker calls after each change of its state, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The breaker calls the callback after it releases its lock, so the callback can read
    /// <see cref="CircuitBreaker.State"/>, and can call <see cref="CircuitBreaker.Isolate"/> or
    /// <see cref="CircuitBreaker.Close"/>.
    /// </para>
    /// <para>
    /// A pipeline awaits the callback, and an exception from the callback stops the call. The
    /// <see cref="CircuitBreaker.Isolate"/> and <see cref="CircuitBreaker.Close"/> methods block until the callback
    /// completes, so the callback must be quick.
    /// </para>
    /// </remarks>
    public Func<CircuitStateChange, ValueTask>? OnStateChanged { get; init; }

    /// <summary>
    /// Gets the time provider that gives the current time for the sampling window and the break.
    /// </summary>
    /// <remarks>The default is <see cref="TimeProvider.System"/>. A test can use a fake time provider.</remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
