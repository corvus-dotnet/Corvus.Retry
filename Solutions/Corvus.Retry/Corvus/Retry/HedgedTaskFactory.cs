// <copyright file="HedgedTaskFactory.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// Runs operations with hedging: the factory starts more attempts in parallel when an attempt is slow or fails, and the
/// first attempt that succeeds wins.
/// </summary>
/// <remarks>
/// <para>
/// Hedging is not a retry. The factory does not wait for an attempt to fail before it starts the next one: it starts
/// the next attempt when <see cref="Stagger"/> expires, or at once when an attempt fails. When every attempt fails, the
/// call throws the exception of the last attempt.
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
/// timeout. To retry and hedge in one call, run a <see cref="RetryTaskFactory"/> inside the operation of a hedged
/// factory, or the reverse.
/// </para>
/// <para>
/// To treat a result as a failure, or to use a fallback, use <see cref="HedgedTaskFactory{T}"/>.
/// </para>
/// </remarks>
public sealed class HedgedTaskFactory
{
    private const string NestedTaskMessage = "Run received an operation that returns a task inside its task. Await the task in the operation.";

    private readonly IRetryPolicy policy = AnyExceptionPolicy.Default;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly TimeProvider? timeProvider;

    /// <summary>
    /// Initialises a new instance of the <see cref="HedgedTaskFactory"/> class.
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
        this.ExtraAttempts = ValidateExtraAttempts(extraAttempts);
        this.Stagger = ValidateStagger(stagger);
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
    /// <see cref="RetryTimeoutException"/>.
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
    /// The breaker counts each attempt as one call, and an attempt timeout as a failure. When the breaker rejects an
    /// attempt, the attempt fails with a <see cref="CircuitRejectedException"/>. Many factories can share one breaker.
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
    /// Runs an operation that returns no result with hedging.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels, when a timeout expires, or when
    /// another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">The total timeout expired.</exception>
    /// <remarks>
    /// When every attempt fails, the exception of the last attempt is rethrown with its original stack trace. For an
    /// operation that returns a <see cref="ValueTask"/>, write <c>ct => DoAsync(ct).AsTask()</c>.
    /// </remarks>
    public Task Run(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ValueTask<Unit> call = HedgingEngine.RunAsync(
            token => new RetryOperations.TaskTokenUnitOperation(operation).InvokeAsync(token),
            null,
            null,
            this.CreateOptions(),
            cancellationToken,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext);

        // A call that completed synchronously needs no task of its own. Otherwise the engine's task carries the outcome.
        return call.IsCompletedSuccessfully ? Task.CompletedTask : call.AsTask();
    }

    /// <summary>
    /// Runs an operation that returns a result with hedging.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels, when a timeout expires, or when
    /// another attempt wins.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">The total timeout expired.</exception>
    /// <remarks>
    /// <para>
    /// When every attempt fails, the exception of the last attempt is rethrown with its original stack trace. The
    /// factory disposes the result of an attempt that does not win, if it implements <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// For an operation that returns a <see cref="ValueTask{TResult}"/>, write <c>ct => GetAsync(ct).AsTask()</c>. To
    /// treat a result as a failure, or to use a fallback, use <see cref="HedgedTaskFactory{T}"/>.
    /// </para>
    /// </remarks>
    public Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return HedgingEngine.RunAsync(
            token => new RetryOperations.TaskTokenOperation<T>(operation).InvokeAsync(token),
            null,
            null,
            this.CreateOptions(),
            cancellationToken,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext).AsTask();
    }

    /// <summary>
    /// Stops the compiler from binding an operation that returns a task inside its task to
    /// <see cref="Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the task of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>Run(async ct => GetAsync(ct))</c> binds to
    /// <see cref="Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/> with the task of <c>GetAsync</c> as
    /// the result. The attempt does not wait for that task, so the first attempt to return a task wins, whatever the
    /// outcome of that task.
    /// </para>
    /// <para>Write <c>Run(async ct => await GetAsync(ct))</c>, or <c>Run(ct => GetAsync(ct))</c>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task<TTask> Run<TTask>(Func<CancellationToken, Task<TTask>> operation, CancellationToken cancellationToken = default, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedTaskMessage);
    }

    /// <summary>
    /// Checks the number of extra attempts.
    /// </summary>
    /// <param name="extraAttempts">The number of extra attempts.</param>
    /// <returns>The number, unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The number is not from 1 thru 10.</exception>
    internal static int ValidateExtraAttempts(int extraAttempts)
    {
        if (extraAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extraAttempts),
                extraAttempts,
                "The number of extra attempts must be from 1 thru 10.");
        }

        return extraAttempts;
    }

    /// <summary>
    /// Checks the stagger, and gives the default for <see langword="null"/>.
    /// </summary>
    /// <param name="stagger">The stagger, or <see langword="null"/> for the default of 2 seconds.</param>
    /// <returns>The stagger.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The stagger is out of range.</exception>
    internal static TimeSpan ValidateStagger(TimeSpan? stagger)
    {
        TimeSpan value = stagger ?? TimeSpan.FromSeconds(2);
        if (value != TimeSpan.Zero && value != Timeout.InfiniteTimeSpan && (value < TimeSpan.Zero || value > RetryDelay.MaxSupported))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stagger),
                value,
                $"The stagger must be TimeSpan.Zero, Timeout.InfiniteTimeSpan, or greater than zero and not greater than {RetryDelay.MaxSupported}.");
        }

        return value;
    }

    /// <summary>
    /// Makes the settings of hedging that the engine takes.
    /// </summary>
    /// <param name="extraAttempts">The number of extra attempts.</param>
    /// <param name="stagger">The stagger.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="onHedgedAttempt">The callback before each extra attempt, or <see langword="null"/>.</param>
    /// <returns>The settings.</returns>
    internal static HedgingOptions CreateOptions(int extraAttempts, TimeSpan stagger, IRetryPolicy policy, Func<HedgedAttemptEventArgs, ValueTask>? onHedgedAttempt)
    {
        return new HedgingOptions
        {
            MaxHedgedAttempts = extraAttempts,
            Delay = stagger,
            Policy = policy,
            OnHedging = onHedgedAttempt,
        };
    }

    private HedgingOptions CreateOptions()
    {
        return CreateOptions(this.ExtraAttempts, this.Stagger, this.policy, this.OnHedgedAttempt);
    }
}
