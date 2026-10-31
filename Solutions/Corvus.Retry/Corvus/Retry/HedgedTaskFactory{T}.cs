// <copyright file="HedgedTaskFactory{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// Runs operations that return a <typeparamref name="T"/> with hedging: the factory starts more attempts in parallel
/// when an attempt is slow or fails, and the first outcome that is not a failure wins. A fallback can give a value when
/// every attempt fails.
/// </summary>
/// <typeparam name="T">The type of the result of the operation.</typeparam>
/// <remarks>
/// <para>
/// Hedging is not a retry. The factory does not wait for an attempt to fail before it starts the next one: it starts
/// the next attempt when <see cref="Stagger"/> expires, or at once when an attempt fails. When every attempt fails, the
/// call throws the last exception or returns the last result. The fallback applies to that outcome.
/// </para>
/// <para>
/// The operation must be idempotent. It must also be safe when two or more attempts run at the same time.
/// </para>
/// <para>
/// A factory is immutable and safe for many threads. Each call has its own state, so one factory can serve many calls
/// at the same time. Set its properties with an object initialiser.
/// </para>
/// <para>
/// The total timeout applies to all the attempts. Each attempt goes through the circuit breaker and has its own attempt
/// timeout. To retry and hedge in one call, run a <see cref="RetryTaskFactory{T}"/> inside the operation of a hedged
/// factory, or the reverse.
/// </para>
/// </remarks>
public sealed class HedgedTaskFactory<T>
{
    private readonly IRetryPolicy policy = AnyExceptionPolicy.Default;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly TimeProvider? timeProvider;

    /// <summary>
    /// Initialises a new instance of the <see cref="HedgedTaskFactory{T}"/> class.
    /// </summary>
    /// <param name="extraAttempts">
    /// The largest number of attempts that the factory starts after the first attempt. The default is 1. The value must
    /// be from 1 thru 10.
    /// </param>
    /// <param name="stagger">
    /// The time after the start of an attempt at which the factory starts the next attempt, if no outcome has won, or
    /// <see langword="null"/> for the default of 2 seconds. See <see cref="Stagger"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="extraAttempts"/> or <paramref name="stagger"/> is out of range.
    /// </exception>
    /// <remarks>
    /// The <see cref="ArgumentException.ParamName"/> of each exception is the name of the parameter that is not correct.
    /// </remarks>
    public HedgedTaskFactory(int extraAttempts = 1, TimeSpan? stagger = null)
    {
        this.ExtraAttempts = HedgedTaskFactory.ValidateExtraAttempts(extraAttempts);
        this.Stagger = HedgedTaskFactory.ValidateStagger(stagger);
    }

    /// <summary>
    /// Gets the largest number of attempts that the factory starts after the first attempt.
    /// </summary>
    /// <remarks>The total number of attempts is <see cref="ExtraAttempts"/> plus 1.</remarks>
    public int ExtraAttempts { get; }

    /// <summary>
    /// Gets the time after the start of an attempt at which the factory starts the next attempt, if no outcome has won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is 2 seconds. The stagger starts again each time an attempt starts. When an attempt fails, the next
    /// attempt starts immediately.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> starts all the attempts at once. <see cref="Timeout.InfiniteTimeSpan"/> starts the
    /// next attempt only after a failure. Any other value must be greater than zero and not greater than about 49.7
    /// days.
    /// </para>
    /// <para>
    /// No attempt starts after an outcome wins. Thus, with <see cref="TimeSpan.Zero"/>, a first attempt that completes
    /// synchronously and wins stops the other attempts from starting.
    /// </para>
    /// <para>
    /// The first attempt runs on the caller's thread until its first await. Each later attempt starts on the thread
    /// pool, with no <see cref="SynchronizationContext"/>. Thus an attempt that blocks the thread does not stop the
    /// next attempt from starting.
    /// </para>
    /// </remarks>
    public TimeSpan Stagger { get; }

    /// <summary>
    /// Gets the policy that decides whether an exception from an attempt is a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. A failure starts the next attempt. An exception that
    /// the policy refuses is not a failure: the factory throws it immediately, and cancels the other attempts.
    /// </para>
    /// <para>
    /// The factory asks the policy about the exceptions that the attempt threw, not the
    /// <see cref="AggregateException"/> around them: for an <see cref="AggregateException"/>, it asks about each inner
    /// exception, and the aggregate is a failure only if the policy accepts every one of them. An aggregate with no inner
    /// exceptions is itself the failure. <see cref="OnHedgedAttempt"/> and the caller still receive the exception as the
    /// attempt threw it.
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
    /// Gets a callback that the factory awaits before it starts each attempt after the first, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback receives the number of the attempt that is about to start, and the exception that caused it. The
    /// exception is <see langword="null"/> when the stagger expired or when a result was a failure. An exception from
    /// the callback stops the call. The callback can run for an attempt that does not start, when another attempt wins
    /// while the callback runs.
    /// </remarks>
    public Func<HedgedAttemptEventArgs, ValueTask>? OnHedgedAttempt { get; init; }

    /// <summary>
    /// Gets the time limit for each attempt, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the factory cancels the token of the attempt, and the attempt fails with a
    /// <see cref="RetryTimeoutException"/>. The factory does not wait for the attempts that it cancels.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public TimeSpan? AttemptTimeout
    {
        get => this.attemptTimeout;
        init => this.attemptTimeout = RetryTaskFactory.ValidateTimeout(value, nameof(this.AttemptTimeout));
    }

    /// <summary>
    /// Gets the time limit for all the attempts, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the factory cancels the token of each attempt, and the call throws a
    /// <see cref="RetryTimeoutException"/>. The fallback can apply to that exception.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public TimeSpan? TotalTimeout
    {
        get => this.totalTimeout;
        init => this.totalTimeout = RetryTaskFactory.ValidateTimeout(value, nameof(this.TotalTimeout));
    }

    /// <summary>
    /// Gets the circuit breaker that permits each attempt, or <see langword="null"/> for no circuit breaker.
    /// </summary>
    /// <remarks>
    /// The breaker counts each attempt as one call, and an attempt timeout or a result that
    /// <see cref="ResultIsFailure"/> rejects as a failure. When the breaker rejects an attempt, the attempt fails with a
    /// <see cref="CircuitRejectedException"/>. Many factories can share one breaker.
    /// </remarks>
    public CircuitBreaker? CircuitBreaker { get; init; }

    /// <summary>
    /// Gets the time provider for the stagger and the timeouts.
    /// </summary>
    /// <remarks>The default is <see cref="TimeProvider.System"/>. A test can use a fake time provider.</remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public TimeProvider TimeProvider
    {
        get => this.timeProvider ?? RetryDefaults.TimeProvider;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(this.TimeProvider));
            this.timeProvider = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the factory continues on the captured context after each await.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. Each attempt after the first starts on the thread pool, so it has no
    /// captured context.
    /// </remarks>
    public bool ContinueOnCapturedContext { get; init; }

    /// <summary>
    /// Gets a predicate that returns <see langword="true"/> for a result that is a failure, or <see langword="null"/>
    /// if no result is a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A result that the predicate rejects does not win: it starts the next attempt, as an exception that the policy
    /// accepts does. When every attempt fails and the last outcome is such a result, the call returns that result, and
    /// the fallback can apply to it. An exception from the predicate stops the call.
    /// </para>
    /// <para>
    /// The factory disposes a rejected result that it does not return, if it implements <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public Func<T, bool>? ResultIsFailure { get; init; }

    /// <summary>
    /// Gets the fallback that gives a value when every attempt fails, or <see langword="null"/> for no fallback.
    /// </summary>
    /// <remarks>
    /// The fallback is the outermost part of a call. It receives the exception or the last result after the hedged
    /// attempts, the total timeout and the circuit breaker. It never applies when the caller cancels the token.
    /// </remarks>
    public Fallback<T>? Fallback { get; init; }

    /// <summary>
    /// Gets a function that gives the operation for each attempt, or <see langword="null"/> to use the primary
    /// operation for every attempt.
    /// </summary>
    /// <remarks>
    /// The function receives the number of the attempt, starting at 1, and can give a different operation for each
    /// attempt (for example, a different endpoint). When it returns <see langword="null"/>, the attempt uses the primary
    /// operation: the operation that the caller gives to <see cref="Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>.
    /// An exception from the function stops the call.
    /// </remarks>
    public Func<int, Func<CancellationToken, Task<T>>?>? OperationForAttempt { get; init; }

    /// <summary>
    /// Runs an operation with hedging.
    /// </summary>
    /// <param name="operation">
    /// The primary operation. It receives a token that is cancelled when the caller cancels, when a timeout expires, or
    /// when another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    /// The result of the first attempt whose outcome is not a failure, or the last result if every attempt failed and
    /// the last outcome was a result. When the fallback applies, the value of its <see cref="Fallback{T}.Action"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">The total timeout expired.</exception>
    /// <remarks>
    /// <para>
    /// When every attempt fails and the last outcome is an exception, that exception is rethrown with its original stack
    /// trace. The factory disposes the result of an attempt that does not win, if it implements
    /// <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// When <see cref="Fallback"/> is set, the fallback receives that exception or that result. It does not receive a
    /// cancellation that the caller caused, or an exception from a callback, a policy or a predicate of the factory.
    /// </para>
    /// <para>
    /// For an operation that returns a <see cref="ValueTask{TResult}"/>, write <c>ct => GetAsync(ct).AsTask()</c>.
    /// </para>
    /// </remarks>
    public Task<T> Run(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // The engine takes operations that return a ValueTask, so adapt the delegates once for this call.
        Func<int, Func<CancellationToken, ValueTask<T>>?>? generator = null;
        if (this.OperationForAttempt is { } operationForAttempt)
        {
            generator = attempt => operationForAttempt(attempt) is { } attemptOperation
                ? token => new RetryOperations.TaskTokenOperation<T>(attemptOperation).InvokeAsync(token)
                : null;
        }

        // The fallback handles only an exception that the engine marks as the outcome of the attempts, so only a call
        // with a fallback needs the marker.
        CallOutcome? outcome = this.Fallback is null ? null : new CallOutcome();
        ValueTask<T> core = HedgingEngine.RunAsync(
            token => new RetryOperations.TaskTokenOperation<T>(operation).InvokeAsync(token),
            generator,
            this.ResultIsFailure,
            HedgedTaskFactory.CreateOptions(this.ExtraAttempts, this.Stagger, this.policy, this.OnHedgedAttempt),
            cancellationToken,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext,
            outcome);

        // Without a fallback, the call has no extra state machine.
        return (this.Fallback is null ? core : FallbackEngine.WithFallbackAsync(core, this.Fallback, outcome!, cancellationToken, this.ContinueOnCapturedContext)).AsTask();
    }
}
