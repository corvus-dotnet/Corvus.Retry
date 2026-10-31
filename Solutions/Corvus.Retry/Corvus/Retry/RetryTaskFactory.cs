// <copyright file="RetryTaskFactory.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// A task factory for retriable tasks. Use the default factory through <see cref="RetryTask.Factory"/>, or make a
/// configured factory and keep it in a field.
/// </summary>
/// <remarks>
/// This is analogous to the <see cref="TaskFactory"/>, but provides tasks with built-in retry semantics.
/// <para>
/// A factory is immutable and safe for many threads. Each call has its own state, so one factory can serve many calls
/// at the same time. Set its properties with an object initialiser. To make a changed copy, use the copy constructor
/// <see cref="RetryTaskFactory(RetryTaskFactory)"/> with an object initialiser.
/// </para>
/// <para>
/// A factory has two kinds of method:
/// <list type="bullet">
/// <item>
/// <see cref="Run(Func{CancellationToken, Task}, CancellationToken)"/>, <see cref="RunValueTask(Func{CancellationToken, ValueTask}, CancellationToken)"/>
/// and their overloads run an asynchronous operation, and retry it when it fails. They use every setting of the
/// factory. When the strategy or the policy refuses a retry, they rethrow the exception of the last attempt with its
/// original stack trace. Unlike <see cref="Task.Run(Func{Task})"/>, they do not queue the operation to the thread pool:
/// the first attempt runs on the caller's thread until its first <see langword="await"/>.
/// </item>
/// <item>
/// <c>StartNew</c> and its overloads start a task that runs a synchronous action on a scheduler, as
/// <see cref="TaskFactory.StartNew(Action)"/> does, and retry it when it fails. An overload without a strategy or a
/// policy parameter uses the <see cref="Strategy"/> or the <see cref="Policy"/> of the factory. The task faults with an
/// <see cref="AggregateException"/> of the failures, as the remarks below describe. <c>StartNew</c> cannot use
/// <see cref="AttemptTimeout"/>, <see cref="TotalTimeout"/>, <see cref="OnRetry"/> or <see cref="CircuitBreaker"/>.
/// When the factory has one of these settings, each <c>StartNew</c> overload throws an
/// <see cref="InvalidOperationException"/>, and does not run the action.
/// </item>
/// </list>
/// </para>
/// <para>
/// The rest of these remarks describe <c>StartNew</c>.
/// </para>
/// <para>
/// There are three different ways of retrying tasks in this library.
/// <list type="number">
/// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
/// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
/// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
/// </list>
/// </para>
/// <para>
/// After a try fails, the factory first gives the strategy a <see cref="RetryAttempt"/>. Its
/// <see cref="RetryAttempt.AttemptNumber"/> is the number of tries that have failed, starting at 1. Its
/// <see cref="RetryAttempt.PreviousDelay"/> is the delay before the try that failed, or <see cref="TimeSpan.Zero"/>
/// for the first try. Its <see cref="RetryAttempt.Exception"/> is the <see cref="AggregateException"/> of the task of
/// the failed try. If the strategy returns <see langword="null"/>, the operation is not retried. Otherwise the policy
/// is asked about the exceptions that the try actually threw: the exceptions in that <see cref="AggregateException"/>,
/// and in any <see cref="AggregateException"/> that the operation threw. The operation is retried only if the policy
/// accepts all of them.
/// </para>
/// <para>
/// A try that cancels itself with an <see cref="OperationCanceledException"/> for a token other than the caller's has
/// failed: the strategy is given the <see cref="AggregateException"/> that contains it, and the policy is asked about
/// it. A try that acknowledges cancellation of the caller's token cancels the returned task, and the strategy and the
/// policy are not asked. If the strategy or the policy throws, the returned task faults with that exception, or is
/// cancelled if it is an <see cref="OperationCanceledException"/> and cancellation of the caller's token was requested.
/// The operation is not retried.
/// </para>
/// <para>
/// When the operation is not retried, the task faults with an <see cref="AggregateException"/> of the failures. Each
/// failure is the exception that a try threw. An <see cref="AggregateException"/> that the operation threw is one
/// failure: it is not flattened. The task keeps at most 100 failures: the first 99 and the last.
/// </para>
/// <para>
/// Every try is queued to the same scheduler: the <c>scheduler</c> argument, or the <see cref="TaskScheduler.Current"/>
/// of the caller of <c>StartNew</c>. If the scheduler refuses to queue the first try, <c>StartNew</c> throws the
/// <see cref="TaskSchedulerException"/>, and the action does not run. If it refuses a retry, that try fails with the
/// <see cref="TaskSchedulerException"/>, and the strategy and the policy decide whether to retry, as for any other
/// failure. The strategy and the policy run on the thread pool, not on that scheduler.
/// </para>
/// </remarks>
public sealed class RetryTaskFactory
{
    /// <summary>
    /// The strategy of a factory that has no strategy set: up to 5 tries, without delay.
    /// </summary>
    /// <remarks>Strategies hold no state for a call, so one instance serves every call.</remarks>
    internal static readonly IRetryStrategy DefaultStrategy = new Count();

    private const string AsyncActionMessage = "StartNew runs an asynchronous action as async void, and does not retry it. Use Run, or Retriable.RetryAsync.";
    private const string ValueTaskActionMessage = "StartNew does not wait for a ValueTask, and does not retry it. Use RunValueTask, or Retriable.RetryAsync.";
    private const string NestedTaskMessage = "Run received an operation that returns a task inside its task. Await the task in the operation.";
    private const string NestedValueTaskMessage = "RunValueTask received an operation that returns a task inside its ValueTask. Await the task in the operation.";

    // An action has no result, so the task returned for it carries none.
    private static readonly Func<Task, object?> NoResult = _ => null;

    private readonly IRetryStrategy strategy = DefaultStrategy;
    private readonly IRetryPolicy policy = AnyExceptionPolicy.Default;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly TimeProvider? timeProvider;

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTaskFactory"/> class, with the default settings.
    /// </summary>
    /// <remarks>
    /// The factory makes up to 5 tries, without delay, and retries any exception: its <see cref="Strategy"/> is
    /// <c>new Count()</c> and its <see cref="Policy"/> is <see cref="AnyExceptionPolicy.Default"/>. It has no timeouts,
    /// no callback and no circuit breaker.
    /// </remarks>
    public RetryTaskFactory()
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTaskFactory"/> class, with a strategy and a policy.
    /// </summary>
    /// <param name="strategy">The strategy that decides whether to retry, and the delay before the retry.</param>
    /// <param name="policy">The policy that decides whether an exception can be retried.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="strategy"/> or <paramref name="policy"/> is <see langword="null"/>.
    /// </exception>
    public RetryTaskFactory(IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.strategy = strategy;
        this.policy = policy;
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTaskFactory"/> class, with the settings of another factory.
    /// </summary>
    /// <param name="other">The factory whose settings to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Use this constructor with an object initialiser to make a copy that differs in some settings:
    /// <c>new RetryTaskFactory(factory) { AttemptTimeout = TimeSpan.FromSeconds(5) }</c>. The copy shares the strategy,
    /// the policy, the callback and the circuit breaker of <paramref name="other"/>.
    /// </remarks>
    public RetryTaskFactory(RetryTaskFactory other)
    {
        ArgumentNullException.ThrowIfNull(other);

        this.strategy = other.strategy;
        this.policy = other.policy;
        this.attemptTimeout = other.attemptTimeout;
        this.totalTimeout = other.totalTimeout;
        this.timeProvider = other.timeProvider;
        this.OnRetry = other.OnRetry;
        this.CircuitBreaker = other.CircuitBreaker;
        this.ContinueOnCapturedContext = other.ContinueOnCapturedContext;
    }

    /// <summary>
    /// Gets the strategy that decides whether to retry, and the delay before the retry.
    /// </summary>
    /// <remarks>
    /// The default is <c>new Count()</c>: up to 5 tries, without delay. The <c>maxTries</c> of a strategy is the number
    /// of retries plus 1. To make one attempt and no retry, use <c>new Count(1)</c> or <c>new DoNotRetry()</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IRetryStrategy Strategy
    {
        get => this.strategy;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(this.Strategy));
            this.strategy = value;
        }
    }

    /// <summary>
    /// Gets the policy that decides whether an exception can be retried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="AnyExceptionPolicy.Default"/>. The policy applies only to exceptions, not to results.
    /// A cancellation that the caller caused is never retried.
    /// </para>
    /// <para>
    /// The policy is asked about the exceptions that the operation actually threw. When the operation throws an
    /// <see cref="AggregateException"/>, the policy is asked about each exception inside it, depth first, and the
    /// attempt is retried only when the policy accepts all of them. The strategy receives the exception as it was
    /// thrown.
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
    /// Gets the time limit for each attempt, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the factory cancels the token of the attempt. The timeout is cooperative: the factory
    /// waits until the operation stops. The attempt then fails with a <see cref="RetryTimeoutException"/>, which the
    /// policy can retry.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public TimeSpan? AttemptTimeout
    {
        get => this.attemptTimeout;
        init => this.attemptTimeout = ValidateTimeout(value, nameof(this.AttemptTimeout));
    }

    /// <summary>
    /// Gets the time limit for all the attempts and all the delays, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the factory cancels the token of the attempt, or it stops the delay. No other attempt
    /// starts, and the call throws a <see cref="RetryTimeoutException"/>.
    /// </para>
    /// <para>
    /// The value must be greater than zero and not greater than about 49.7 days. Use <see langword="null"/>, not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, for no limit.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public TimeSpan? TotalTimeout
    {
        get => this.totalTimeout;
        init => this.totalTimeout = ValidateTimeout(value, nameof(this.TotalTimeout));
    }

    /// <summary>
    /// Gets a callback that the factory awaits before each delay, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives the number of the attempt that failed, and the exception that caused the retry. The delay
    /// starts only when the callback completes. An exception from the callback stops the call.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    public Func<RetryEventArgs, ValueTask>? OnRetry { get; init; }

    /// <summary>
    /// Gets the circuit breaker that permits each attempt, or <see langword="null"/> for no circuit breaker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The breaker is inside the retry loop and outside the attempt timeout: it counts each attempt as one call, and an
    /// attempt timeout as a failure. Many factories can share one breaker.
    /// </para>
    /// <para>
    /// When the breaker rejects an attempt, the attempt fails with a <see cref="CircuitRejectedException"/>. The
    /// strategy and the policy decide whether to retry it, as for any exception.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    public CircuitBreaker? CircuitBreaker { get; init; }

    /// <summary>
    /// Gets the time provider for the delays and the timeouts.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="TimeProvider.System"/>. A test can use a fake time provider. <c>StartNew</c> also uses
    /// this time provider for its delays.
    /// </remarks>
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
    /// Gets a value indicating whether <c>Run</c> and <c>RunValueTask</c> continue on the captured context after each
    /// await.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. This setting does not apply to <c>StartNew</c>, which queues each try to
    /// its scheduler and makes its retry decisions on the thread pool.
    /// </remarks>
    public bool ContinueOnCapturedContext { get; init; }

    /// <summary>
    /// Runs an operation that returns no result, and retries it when it throws an exception.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses a retry, the exception of the last attempt is rethrown with its original
    /// stack trace.
    /// </para>
    /// <para>
    /// Unlike <see cref="Task.Run(Func{Task})"/>, this method does not queue the operation to the thread pool: the
    /// first attempt runs on the caller's thread until its first <see langword="await"/>.
    /// </para>
    /// </remarks>
    public Task Run(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ValueTask<Unit> call = RetryEngine.RunAsync<RetryOperations.TaskTokenUnitOperation, Unit>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext);

        // A call that completed synchronously needs no task of its own. Otherwise the engine's task carries the outcome.
        return call.IsCompletedSuccessfully ? Task.CompletedTask : call.AsTask();
    }

    /// <summary>
    /// Runs an operation that returns a result, and retries it when it throws an exception.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses a retry, the exception of the last attempt is rethrown with its original
    /// stack trace.
    /// </para>
    /// <para>
    /// Unlike <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>, this method does not queue the operation to the
    /// thread pool: the first attempt runs on the caller's thread until its first <see langword="await"/>.
    /// </para>
    /// <para>
    /// To retry a result that is a failure, or to use a fallback, use <see cref="RetryTaskFactory{T}"/>.
    /// </para>
    /// </remarks>
    public Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RetryEngine.RunAsync<RetryOperations.TaskTokenOperation<T>, T>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext).AsTask();
    }

    /// <summary>
    /// Runs an operation that returns no result as a <see cref="ValueTask"/>, and retries it when it throws an
    /// exception.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when an attempt succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method behaves as <see cref="Run(Func{CancellationToken, Task}, CancellationToken)"/>. When there is no
    /// timeout and no callback, and every attempt completes synchronously, a call allocates nothing.
    /// </para>
    /// </remarks>
    public ValueTask RunValueTask(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ValueTask<Unit> call = RetryEngine.RunAsync<RetryOperations.ValueTaskTokenUnitOperation, Unit>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext);

        // A call that completed synchronously needs no task. Otherwise the task carries the outcome of the call.
        return call.IsCompletedSuccessfully ? default : new ValueTask(call.AsTask());
    }

    /// <summary>
    /// Runs an operation that returns a result as a <see cref="ValueTask{TResult}"/>, and retries it when it throws an
    /// exception.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method behaves as <see cref="Run{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>. When there is
    /// no timeout and no callback, and every attempt completes synchronously, a call allocates nothing.
    /// </para>
    /// </remarks>
    public ValueTask<T> RunValueTask<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RetryEngine.RunAsync<RetryOperations.ValueTaskTokenOperation<T>, T>(
            new(operation),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext);
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
    /// the result. The operation does not wait for that task, so a failure of that task is not retried.
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
    /// Stops the compiler from binding an operation that returns a task inside its <see cref="ValueTask{TResult}"/> to
    /// <see cref="RunValueTask{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of the task inside the <see cref="ValueTask{TResult}"/> of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="arguments">No arguments. This parameter makes the signature different from the overload that this one blocks.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>RunValueTask(async ct => GetAsync(ct))</c> binds to
    /// <see cref="RunValueTask{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/> with the task of
    /// <c>GetAsync</c> as the result. The operation does not wait for that task, so a failure of that task is not
    /// retried.
    /// </para>
    /// <para>Write <c>RunValueTask(async ct => await GetAsync(ct))</c>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(NestedValueTaskMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public ValueTask<TTask> RunValueTask<TTask>(Func<CancellationToken, ValueTask<TTask>> operation, CancellationToken cancellationToken = default, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(NestedValueTaskMessage);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action with cancellation.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, cancellationToken, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action with cancellation.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, taskCreationOptions, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action which is passed a state object.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, state, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action which is passed a state object.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation option.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, state, taskCreationOptions, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action which is passed a state object.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(action);

        return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, cancellationToken, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, taskCreationOptions, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, state, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, state, taskCreationOptions, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, cancellationToken, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, taskCreationOptions, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, state, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, state, taskCreationOptions, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, state, cancellationToken, taskCreationOptions, scheduler, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(action, cancellationToken, taskCreationOptions, scheduler, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action), () => new Task(action), NoResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, cancellationToken), () => new Task(action, cancellationToken), NoResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, Detached(taskCreationOptions)), () => new Task(action, Detached(taskCreationOptions)), NoResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, state), () => new Task(action, state), NoResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, state, Detached(taskCreationOptions)), () => new Task(action, state, Detached(taskCreationOptions)), NoResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, state, cancellationToken), () => new Task(action, state, cancellationToken), NoResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action<object?> action, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, state, cancellationToken, Detached(taskCreationOptions), scheduler), () => new Task(action, state, cancellationToken, Detached(taskCreationOptions)), NoResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for an action.
    /// </summary>
    /// <param name="action">The action for which to start a retriable tasks.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the action is complete.</returns>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory"/>.
    /// </exception>
    public Task StartNew(Action action, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        this.ThrowIfStartNewCannotHonourSettings();

        return Start(Task.Factory.StartNew(action, cancellationToken, Detached(taskCreationOptions), scheduler), () => new Task(action, cancellationToken, Detached(taskCreationOptions)), NoResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Stops the compiler from binding an asynchronous action to <see cref="StartNew(Action)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TTask">The type of task that the action returns.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>StartNew(async () => ...)</c> binds to <see cref="StartNew(Action)"/>, and the
    /// lambda runs as <c>async void</c>. The returned task completes at the first <see langword="await"/>, and an
    /// exception after it is not retried and can stop the process. This overload has a higher priority than the
    /// <see cref="Action"/> overloads, so the compiler reports an error. A lambda that only throws has no return type,
    /// so it does not bind to this overload.
    /// </para>
    /// <para>Use <see cref="Run(Func{CancellationToken, Task}, CancellationToken)"/>, or <see cref="Retriable.RetryAsync(Func{Task})"/> or one of its overloads.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TTask>(Func<TTask> asyncAction, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(AsyncActionMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an asynchronous action to <see cref="StartNew(Action{object?}, object?)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TTask">The type of task that the action returns.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="state">The state for the action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// Without this overload, <c>StartNew(async state => ..., state)</c> binds to
    /// <see cref="StartNew(Action{object?}, object?)"/>, and the lambda runs as <c>async void</c>. This overload has a
    /// higher priority than the <see cref="Action{T}"/> overloads, so the compiler reports an error.
    /// </para>
    /// <para>Use <see cref="Run(Func{CancellationToken, Task}, CancellationToken)"/>, or <see cref="Retriable.RetryAsync(Func{Task})"/> or one of its overloads.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(AsyncActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TTask>(Func<object?, TTask> asyncAction, object? state, params object?[] arguments)
        where TTask : Task
    {
        throw new NotSupportedException(AsyncActionMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an action that returns a <see cref="ValueTask"/> to
    /// <see cref="StartNew(Action)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TValueTask">The type that the action returns. Only <see cref="ValueTask"/> meets the constraints.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// <para>
    /// The returned task of <see cref="StartNew(Action)"/> completes when the action returns its
    /// <see cref="ValueTask"/>. It does not wait for the operation, and does not retry a failure after the first
    /// <see langword="await"/>. This overload has a higher priority than the <see cref="Action"/> overloads, so the
    /// compiler reports an error.
    /// </para>
    /// <para>
    /// This method is generic, so that a lambda that only throws does not bind to it. Its last parameter is a span,
    /// so that its signature is different from <see cref="StartNew{TTask}(Func{TTask}, object?[])"/>.
    /// </para>
    /// <para>Use <see cref="RunValueTask(Func{CancellationToken, ValueTask}, CancellationToken)"/>, or <see cref="Retriable.RetryAsync(Func{Task})"/>.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(ValueTaskActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TValueTask>(Func<TValueTask> asyncAction, params ReadOnlySpan<object?> arguments)
        where TValueTask : struct, IEquatable<ValueTask>
    {
        throw new NotSupportedException(ValueTaskActionMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an action that returns a <see cref="ValueTask{TResult}"/> to
    /// <see cref="StartNew(Action)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the action.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// See <see cref="StartNew{TValueTask}(Func{TValueTask}, ReadOnlySpan{object?})"/> for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(ValueTaskActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TResult>(Func<ValueTask<TResult>> asyncAction, params object?[] arguments)
    {
        throw new NotSupportedException(ValueTaskActionMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an action that returns a <see cref="ValueTask"/> to
    /// <see cref="StartNew(Action{object?}, object?)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TValueTask">The type that the action returns. Only <see cref="ValueTask"/> meets the constraints.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="state">The state for the action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// See <see cref="StartNew{TValueTask}(Func{TValueTask}, ReadOnlySpan{object?})"/> for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(ValueTaskActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TValueTask>(Func<object?, TValueTask> asyncAction, object? state, params ReadOnlySpan<object?> arguments)
        where TValueTask : struct, IEquatable<ValueTask>
    {
        throw new NotSupportedException(ValueTaskActionMessage);
    }

    /// <summary>
    /// Stops the compiler from binding an action that returns a <see cref="ValueTask{TResult}"/> to
    /// <see cref="StartNew(Action{object?}, object?)"/> or its overloads.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the action.</typeparam>
    /// <param name="asyncAction">The asynchronous action.</param>
    /// <param name="state">The state for the action.</param>
    /// <param name="arguments">The other arguments of the call.</param>
    /// <returns>This method does not return.</returns>
    /// <remarks>
    /// See <see cref="StartNew{TValueTask}(Func{TValueTask}, ReadOnlySpan{object?})"/> for the reason.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always. The compiler does not let a call bind to this method.</exception>
    [Obsolete(ValueTaskActionMessage, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [OverloadResolutionPriority(1)]
    public Task StartNew<TResult>(Func<object?, ValueTask<TResult>> asyncAction, object? state, params object?[] arguments)
    {
        throw new NotSupportedException(ValueTaskActionMessage);
    }

    /// <summary>
    /// Returns a task that completes when the first try, and any retries, of an operation have finished.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the returned task.</typeparam>
    /// <param name="firstTry">The task running the first try of the operation.</param>
    /// <param name="createTask">A function that creates an unstarted task for each retry of the operation.</param>
    /// <param name="getResult">A function that gets the result from the task of a successful try.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The caller's task creation options.</param>
    /// <param name="scheduler">The scheduler on which every try runs.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <param name="timeProvider">The time provider for the delays: the <see cref="TimeProvider"/> of the factory.</param>
    /// <returns>
    /// A task that completes with the result of the first successful try; faults with the exceptions of the failed
    /// tries (the first 99 and the last, when more than 100 tries fail) when the operation will not be retried; or is
    /// cancelled, with <paramref name="cancellationToken"/>, when cancellation stops the operation.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The tries must be created with <see cref="Detached(TaskCreationOptions)"/> options. If the caller asked for
    /// <see cref="TaskCreationOptions.AttachedToParent"/>, it is the returned task that attaches to the parent, so
    /// that the parent sees the outcome of the operation rather than the failures of tries that were retried. The
    /// returned task also honours <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.
    /// </para>
    /// <para>
    /// No thread is blocked while the operation is retried. Retry decisions run on the thread pool, each delay is a
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <paramref name="timeProvider"/> with a
    /// continuation, and each retry is queued to <paramref name="scheduler"/>.
    /// </para>
    /// <para>
    /// This is used by both the <see cref="RetryTaskFactory"/> and the <see cref="RetryTaskFactory{T}"/>.
    /// </para>
    /// </remarks>
    internal static Task<TResult> Start<TResult>(Task firstTry, Func<Task> createTask, Func<Task, TResult> getResult, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy, TimeProvider timeProvider)
    {
        var operation = new RetryOperation<TResult>(createTask, getResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy, timeProvider);
        operation.ExamineWhenComplete(firstTry);
        return operation.Task;
    }

    /// <summary>
    /// Gets the options with which to create each try of an operation.
    /// </summary>
    /// <param name="taskCreationOptions">The caller's task creation options.</param>
    /// <returns>The caller's options, without <see cref="TaskCreationOptions.AttachedToParent"/>.</returns>
    internal static TaskCreationOptions Detached(TaskCreationOptions taskCreationOptions)
    {
        return taskCreationOptions & ~TaskCreationOptions.AttachedToParent;
    }

    /// <summary>
    /// Checks the value of a timeout setting.
    /// </summary>
    /// <param name="timeout">The value to check.</param>
    /// <param name="propertyName">The name of the property, which becomes the <see cref="ArgumentException.ParamName"/>.</param>
    /// <returns>The value, unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not greater than zero, or is greater than the longest time that a timer supports.
    /// </exception>
    internal static TimeSpan? ValidateTimeout(TimeSpan? timeout, string propertyName)
    {
        // This also rejects Timeout.InfiniteTimeSpan, which is negative.
        if (timeout is TimeSpan value && (value <= TimeSpan.Zero || value > RetryDelay.MaxSupported))
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                $"The timeout must be greater than zero and not greater than {RetryDelay.MaxSupported}. Use null for no timeout.");
        }

        return timeout;
    }

    /// <summary>
    /// Throws when a factory has a setting that <c>StartNew</c> cannot use.
    /// </summary>
    /// <param name="attemptTimeout">The attempt timeout of the factory.</param>
    /// <param name="totalTimeout">The total timeout of the factory.</param>
    /// <param name="onRetry">The retry callback of the factory.</param>
    /// <param name="circuitBreaker">The circuit breaker of the factory.</param>
    /// <param name="canRetryResult">The result predicate of the factory, or <see langword="null"/>.</param>
    /// <param name="fallback">The fallback of the factory, or <see langword="null"/>.</param>
    /// <exception cref="InvalidOperationException">One or more of the settings is not <see langword="null"/>.</exception>
    internal static void ThrowIfStartNewCannotHonour(TimeSpan? attemptTimeout, TimeSpan? totalTimeout, object? onRetry, CircuitBreaker? circuitBreaker, object? canRetryResult, object? fallback)
    {
        if (attemptTimeout is null && totalTimeout is null && onRetry is null && circuitBreaker is null && canRetryResult is null && fallback is null)
        {
            return;
        }

        List<string> settings = [];
        AddIfSet(settings, attemptTimeout, nameof(AttemptTimeout));
        AddIfSet(settings, totalTimeout, nameof(TotalTimeout));
        AddIfSet(settings, onRetry, nameof(OnRetry));
        AddIfSet(settings, circuitBreaker, nameof(CircuitBreaker));
        AddIfSet(settings, canRetryResult, nameof(RetryTaskFactory<object>.CanRetryResult));
        AddIfSet(settings, fallback, nameof(RetryTaskFactory<object>.Fallback));

        string names = settings.Count == 1
            ? settings[0]
            : $"{string.Join(", ", settings[..^1])} and {settings[^1]}";
        throw new InvalidOperationException(settings.Count == 1
            ? $"StartNew cannot use the {names} setting of this factory. Use Run or RunValueTask, or a factory without this setting."
            : $"StartNew cannot use the {names} settings of this factory. Use Run or RunValueTask, or a factory without these settings.");

        static void AddIfSet(List<string> settings, object? value, string name)
        {
            if (value is not null)
            {
                settings.Add(name);
            }
        }
    }

    /// <summary>
    /// Throws when this factory has a setting that <c>StartNew</c> cannot use.
    /// </summary>
    /// <exception cref="InvalidOperationException">The factory has such a setting.</exception>
    private void ThrowIfStartNewCannotHonourSettings()
    {
        ThrowIfStartNewCannotHonour(this.attemptTimeout, this.totalTimeout, this.OnRetry, this.CircuitBreaker, null, null);
    }

    /// <summary>
    /// An operation being retried by <see cref="Start{TResult}"/>, and the task that reports its outcome.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <remarks>
    /// <para>
    /// This holds everything the continuations need, so that each continuation can be a static delegate. It also
    /// holds the state of the call (the attempt number, the last delay and the failures), so that the strategy holds
    /// none.
    /// </para>
    /// <para>
    /// Retry decisions run on the thread pool; a delay is a
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="TimeProvider"/> with a
    /// continuation; no thread is blocked. Only one try runs at a time, and each step starts after the previous one
    /// has ended, so the state of the call is never used concurrently.
    /// </para>
    /// </remarks>
    private sealed class RetryOperation<TResult> : TaskCompletionSource<TResult>
    {
        // The most failures that the task reports.
        private const int MaxRecordedFailures = 100;

        // The operation is passed as each continuation's state, so the continuations need no closure.
        private static readonly Action<Task, object?> OnTryCompleted = static (t, state) => ((RetryOperation<TResult>)state!).Complete(t);
        private static readonly Action<Task, object?> OnDelayCompleted = static (t, state) => ((RetryOperation<TResult>)state!).AfterDelay(t);

        private readonly Func<Task> createTask;
        private readonly Func<Task, TResult> getResult;
        private readonly CancellationToken cancellationToken;
        private readonly TaskScheduler scheduler;
        private readonly IRetryStrategy strategy;
        private readonly IRetryPolicy policy;
        private readonly TimeProvider timeProvider;

        // The state of this call. The strategy holds none, so one strategy can serve many calls at the same time.
        private int attemptNumber = 1;
        private TimeSpan previousDelay;

        // Created on the first failure, so that an operation that succeeds at its first try allocates no list.
        private List<Exception>? failures;

        public RetryOperation(Func<Task> createTask, Func<Task, TResult> getResult, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy, TimeProvider timeProvider)
            : base(taskCreationOptions & (TaskCreationOptions.AttachedToParent | TaskCreationOptions.RunContinuationsAsynchronously))
        {
            this.createTask = createTask;
            this.getResult = getResult;
            this.cancellationToken = cancellationToken;
            this.scheduler = scheduler;
            this.strategy = strategy;
            this.policy = policy;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// Examines the outcome of a try when it completes.
        /// </summary>
        /// <param name="task">The task running the try.</param>
        /// <remarks>
        /// The examination runs on the thread pool, not the caller's scheduler, so that it does not wait behind (or
        /// hold up) other work on that scheduler, such as a UI thread.
        /// </remarks>
        public void ExamineWhenComplete(Task task)
        {
            task.ContinueWith(OnTryCompleted, this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void Complete(Task task)
        {
            try
            {
                if (task.IsCanceled)
                {
                    // A try acknowledged cancellation, or the token was cancelled before it could start.
                    this.TrySetCanceled(this.cancellationToken);
                }
                else if (task.Exception is { } exception)
                {
                    this.RetryOrFail(exception);
                }
                else
                {
                    this.TrySetResult(this.getResult(task));
                }
            }
            catch (OperationCanceledException) when (this.cancellationToken.IsCancellationRequested)
            {
                this.TrySetCanceled(this.cancellationToken);
            }
            catch (Exception ex)
            {
                // The strategy or policy failed.
                this.TrySetException(ex);
            }
        }

        private void RetryOrFail(AggregateException exception)
        {
            this.Record(exception);

            TimeSpan? retryDelay = this.strategy.GetRetryDelay(new RetryAttempt(this.attemptNumber, exception, null, this.previousDelay));

            if (retryDelay is null || !this.policy.CanRetryAll(exception))
            {
                // Report the failures of every try, not only the last one.
                this.TrySetException(new AggregateException(this.failures!));
                return;
            }

            // Check before starting a delay for a retry that will not run.
            if (this.cancellationToken.IsCancellationRequested)
            {
                this.TrySetCanceled(this.cancellationToken);
                return;
            }

            TimeSpan delay = RetryDelay.Clamp(retryDelay.Value);
            this.attemptNumber++;
            this.previousDelay = delay;

            if (delay == TimeSpan.Zero)
            {
                this.StartNextTry();
                return;
            }

            // The delay ends early if cancellation is requested. Its continuation only queues the next try, so it is
            // cheap enough to run on the thread that completes the delay.
            RetryDelay.WaitAsync(delay, this.timeProvider, this.cancellationToken).ContinueWith(
                OnDelayCompleted,
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Records the failure of a try.
        /// </summary>
        /// <param name="exception">The exception of the try's task.</param>
        /// <remarks>
        /// <para>
        /// The inner exceptions are recorded, so that the task's own <see cref="AggregateException"/> does not wrap
        /// each failure. An <see cref="AggregateException"/> with no inner exceptions is recorded as itself, so that
        /// the failure is not lost.
        /// </para>
        /// <para>
        /// At most <see cref="MaxRecordedFailures"/> failures are kept, so that a strategy with no limit does not use
        /// more and more memory. The operation keeps the first 99 failures and the last.
        /// </para>
        /// </remarks>
        private void Record(AggregateException exception)
        {
            List<Exception> failures = this.failures ??= [];
            if (exception.InnerExceptions.Count == 0)
            {
                Add(failures, exception);
            }
            else
            {
                foreach (Exception inner in exception.InnerExceptions)
                {
                    Add(failures, inner);
                }
            }

            static void Add(List<Exception> failures, Exception failure)
            {
                if (failures.Count < MaxRecordedFailures)
                {
                    failures.Add(failure);
                }
                else
                {
                    failures[^1] = failure;
                }
            }
        }

        private void AfterDelay(Task delay)
        {
            try
            {
                if (delay.IsCanceled || this.cancellationToken.IsCancellationRequested)
                {
                    this.TrySetCanceled(this.cancellationToken);
                }
                else
                {
                    this.StartNextTry();
                }
            }
            catch (Exception ex)
            {
                // Nothing observes this continuation's task, so a failure must fault the operation instead.
                this.TrySetException(ex);
            }
        }

        private void StartNextTry()
        {
            Task next = this.createTask();
            if (next.IsCanceled)
            {
                // A task created with a cancelled token is cancelled before it starts.
                this.TrySetCanceled(this.cancellationToken);
                return;
            }

            // Attach the continuation before the try starts, so that the try cannot complete before it is attached.
            this.ExamineWhenComplete(next);

            try
            {
                next.Start(this.scheduler);
            }
            catch (InvalidOperationException) when (next.IsCanceled)
            {
                // The token was cancelled after the check above, but before the retry could start.
                this.TrySetCanceled(this.cancellationToken);
            }
            catch (TaskSchedulerException)
            {
                // The scheduler could not queue the retry, so the retry's task is faulted with this exception. Its
                // continuation examines that failure like any other, so there is nothing more to do here.
            }
        }
    }
}
