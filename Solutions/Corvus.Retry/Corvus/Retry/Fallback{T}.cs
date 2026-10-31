// <copyright file="Fallback{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The fallback of a factory: the value that the factory gives when all its attempts fail.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <para>
/// The fallback is the outermost part of a call. It receives the exception or the result after the retries (or the
/// hedged attempts), the total timeout and the circuit breaker. It applies to a <see cref="RetryTimeoutException"/> and
/// to a <see cref="CircuitRejectedException"/>. It never applies when the caller cancels the token.
/// </para>
/// <para>
/// The fallback applies only to the outcome of the attempts. It does not apply to an exception from a callback, a
/// strategy or a policy of the factory, for example the result predicate, the retry callback, the strategy, the retry
/// policy, the policy or the state change callback of the circuit breaker, the hedged attempt callback, or the
/// function that gives the operation for each hedged attempt. Such an exception stops the call.
/// </para>
/// <para>
/// A fallback is immutable. Each <see langword="init"/> accessor checks its value.
/// </para>
/// </remarks>
public sealed class Fallback<T>
{
    private readonly IRetryPolicy policy = AnyExceptionPolicy.Default;

    /// <summary>
    /// Initialises a new instance of the <see cref="Fallback{T}"/> class.
    /// </summary>
    /// <param name="action">
    /// The action that gives the fallback value. The factory returns the value of the action. An exception from the
    /// action stops the call; the factory does not catch it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public Fallback(Func<FallbackEventArgs<T>, ValueTask<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        this.Action = action;
    }

    /// <summary>
    /// Gets the action that gives the fallback value.
    /// </summary>
    /// <remarks>
    /// The factory returns the value of the action. An exception from the action stops the call; the factory does not
    /// catch it.
    /// </remarks>
    public Func<FallbackEventArgs<T>, ValueTask<T>> Action { get; }

    /// <summary>
    /// Gets the policy that decides whether an exception from the attempts gets the fallback value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. When the policy refuses an exception, the factory
    /// throws that exception.
    /// </para>
    /// <para>
    /// The policy is asked about the exceptions that the operation actually threw. When the exception is an
    /// <see cref="AggregateException"/>, the policy is asked about each exception inside it, depth first, and the
    /// fallback applies only when the policy accepts all of them. <see cref="FallbackEventArgs{T}.Exception"/> is the
    /// <see cref="AggregateException"/> itself.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IRetryPolicy Policy
    {
        get => this.policy;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(this.Policy));
            this.policy = value;
        }
    }

    /// <summary>
    /// Gets a predicate that returns <see langword="true"/> for a result that gets the fallback value, or
    /// <see langword="null"/> if no result gets the fallback value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate receives the result that the attempts return. For a rejected result, the factory calls
    /// <see cref="OnFallback"/> with the result, then disposes the result if it implements <see cref="IDisposable"/>,
    /// and then calls <see cref="Action"/>. Thus <see cref="OnFallback"/> can examine the result, for example to log
    /// the body of a response, and <see cref="Action"/> receives a result that is already disposed. This is the order
    /// of the retries, which dispose a rejected result after the retry callback. An exception from the disposal stops
    /// the call: the caller gets that exception, and the factory does not call <see cref="Action"/>.
    /// </para>
    /// <para>
    /// An exception from the predicate stops the call: the caller gets that exception. The factory disposes the result
    /// that it does not return, and ignores an exception from that disposal.
    /// </para>
    /// </remarks>
    public Func<T, bool>? AppliesToResult { get; init; }

    /// <summary>
    /// Gets a callback that the factory awaits before it calls <see cref="Action"/>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback receives the exception or the result that caused the fallback. A rejected result is not yet
    /// disposed when the callback runs. An exception from the callback stops the call, and the factory does not call
    /// <see cref="Action"/>; the factory disposes a rejected result, and ignores an exception from that disposal.
    /// </remarks>
    public Func<FallbackEventArgs<T>, ValueTask>? OnFallback { get; init; }
}
