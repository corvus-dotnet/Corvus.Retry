// <copyright file="RetryStrategies.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Net.Http;

/// <summary>
/// Creates <see cref="IRetryStrategy"/> instances, and provides shared ones.
/// </summary>
/// <remarks>
/// <para>
/// The methods of this class count retries, not attempts: the total number of attempts is the number of retries
/// plus 1. A value of 0 for <c>retries</c> gives one attempt and no retry. (The constructors of the strategy classes
/// count all the attempts, with a <c>maxTries</c> parameter.)
/// </para>
/// <para>
/// Every strategy limits its delays to the maximum delay, if one is given, and then to the longest delay that a timer
/// supports (about 49.7 days). Strategies hold no state for a call, so one instance can be shared.
/// </para>
/// </remarks>
public static class RetryStrategies
{
    /// <summary>
    /// Gets the strategy that the overloads without a strategy parameter use: up to 10 tries, without delay.
    /// </summary>
    /// <remarks>This is one shared <see cref="Count"/> instance. Strategies hold no state for a call, so it is safe to share.</remarks>
    public static IRetryStrategy Default { get; } = new Count(10);

    /// <summary>
    /// Creates a strategy with the same delay before every retry.
    /// </summary>
    /// <param name="retries">The number of retries. 0 gives one attempt and no retry.</param>
    /// <param name="delay">The delay before each retry.</param>
    /// <param name="maxDelay">The longest delay, or <see langword="null"/> for no limit.</param>
    /// <param name="useJitter">
    /// <see langword="true"/> to change each delay by a random value between -25% and +25%, before
    /// <paramref name="maxDelay"/> applies.
    /// </param>
    /// <returns>A strategy whose delay before retry n is <c>delay</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retries"/>, <paramref name="delay"/> or <paramref name="maxDelay"/> is negative.
    /// </exception>
    public static IRetryStrategy Constant(int retries, TimeSpan delay, TimeSpan? maxDelay = null, bool useJitter = false)
    {
        int maxTries = ToMaxTries(retries);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ThrowIfNegative(maxDelay);

        // The class name is qualified, because the method Linear hides it here.
        return new Strategies.Linear(delay, maxTries, maxDelay, useJitter);
    }

    /// <summary>
    /// Creates a strategy that increases the delay by the same amount before each retry.
    /// </summary>
    /// <param name="retries">The number of retries. 0 gives one attempt and no retry.</param>
    /// <param name="delay">The delay before the first retry, and the increase before each later retry.</param>
    /// <param name="maxDelay">The longest delay, or <see langword="null"/> for no limit.</param>
    /// <param name="useJitter">
    /// <see langword="true"/> to change each delay by a random value between -25% and +25%, before
    /// <paramref name="maxDelay"/> applies.
    /// </param>
    /// <returns>A strategy whose delay before retry n is <c>delay * n</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retries"/>, <paramref name="delay"/> or <paramref name="maxDelay"/> is negative.
    /// </exception>
    /// <remarks>
    /// The <see cref="Strategies.Linear"/> class has a different formula: its delay is the same before every retry. Use
    /// <see cref="Constant(int, TimeSpan, TimeSpan?, bool)"/> for that formula.
    /// </remarks>
    public static IRetryStrategy Linear(int retries, TimeSpan delay, TimeSpan? maxDelay = null, bool useJitter = false)
    {
        int maxTries = ToMaxTries(retries);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ThrowIfNegative(maxDelay);

        return new Incremental(maxTries, delay, delay, maxDelay, useJitter);
    }

    /// <summary>
    /// Creates a strategy that doubles the delay before each retry.
    /// </summary>
    /// <param name="retries">The number of retries. 0 gives one attempt and no retry.</param>
    /// <param name="baseDelay">The delay before the first retry.</param>
    /// <param name="maxDelay">The longest delay, or <see langword="null"/> for no limit.</param>
    /// <param name="useJitter"><see langword="true"/> to use decorrelated jitter.</param>
    /// <returns>
    /// A strategy whose delay before retry n is <c>baseDelay * 2^(n - 1)</c>. With jitter, the delay is
    /// <c>min(maxDelay, random(baseDelay, previous * 3))</c>. See <see cref="Strategies.Exponential"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retries"/>, <paramref name="baseDelay"/> or <paramref name="maxDelay"/> is negative.
    /// </exception>
    public static IRetryStrategy Exponential(int retries, TimeSpan baseDelay, TimeSpan? maxDelay = null, bool useJitter = false)
    {
        int maxTries = ToMaxTries(retries);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        ThrowIfNegative(maxDelay);

        return new Strategies.Exponential(maxTries, baseDelay, maxDelay, useJitter);
    }

    /// <summary>
    /// Creates a strategy that asks a delegate for the delay.
    /// </summary>
    /// <param name="getRetryDelay">
    /// The delegate that gives the delay before the next attempt, or <see langword="null"/> to stop. It can be called
    /// by many calls at the same time, so it must not hold state for a call.
    /// </param>
    /// <returns>A strategy that uses the delegate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getRetryDelay"/> is <see langword="null"/>.</exception>
    public static IRetryStrategy FromDelegate(Func<RetryAttempt, TimeSpan?> getRetryDelay)
    {
        ArgumentNullException.ThrowIfNull(getRetryDelay);

        return new DelegateRetryStrategy(getRetryDelay);
    }

    /// <summary>
    /// Creates a strategy that uses the <c>Retry-After</c> header of an <see cref="HttpResponseMessage"/> result.
    /// </summary>
    /// <param name="inner">
    /// The strategy that decides whether to retry, and gives the delay when there is no header.
    /// </param>
    /// <param name="timeProvider">
    /// The time provider that gives the current time, for a header with a date. <see langword="null"/> uses
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    /// <returns>A strategy that uses the header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>The strategy calls the inner strategy first. If the inner strategy returns <see langword="null"/>, it returns <see langword="null"/>.</para>
    /// <para>
    /// Otherwise, if <see cref="RetryAttempt.Result"/> is an <see cref="HttpResponseMessage"/> with a
    /// <c>Retry-After</c> header, the header gives the delay: the delta if there is one, or else the date minus the
    /// current time. A date in the past gives a zero delay. If there is no header, the delay of the inner strategy
    /// applies.
    /// </para>
    /// <para>The delay is limited to the longest delay that a timer supports.</para>
    /// </remarks>
    public static IRetryStrategy RetryAfter(IRetryStrategy inner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return new RetryAfterStrategy(inner, timeProvider ?? TimeProvider.System);
    }

    private static int ToMaxTries(int retries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries, nameof(retries));

        // The number of attempts is one more than the number of retries. int.MaxValue retries is unlimited in
        // practice, so it does not overflow.
        return retries == int.MaxValue ? int.MaxValue : retries + 1;
    }

    private static void ThrowIfNegative(TimeSpan? maxDelay)
    {
        if (maxDelay is TimeSpan max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, TimeSpan.Zero, nameof(maxDelay));
        }
    }
}
