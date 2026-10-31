// <copyright file="RetryTaskFactory{T}.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// A task factory for retriable tasks that return a <typeparamref name="T"/>. Use the default factory through
/// <see cref="RetryTask{T}.Factory"/>, or make a configured factory and keep it in a field.
/// </summary>
/// <typeparam name="T">The type of the result of the Task.</typeparam>
/// <remarks>
/// This is analogous to the <see cref="TaskFactory{T}"/>, but provides tasks with built-in retry semantics.
/// <para>
/// A factory is immutable and safe for many threads. Each call has its own state, so one factory can serve many calls
/// at the same time. Set its properties with an object initialiser. To make a changed copy, use the copy constructor
/// <see cref="RetryTaskFactory{T}(RetryTaskFactory{T})"/> with an object initialiser.
/// </para>
/// <para>
/// A factory has two kinds of method:
/// <list type="bullet">
/// <item>
/// <see cref="Run(Func{CancellationToken, Task{T}}, CancellationToken)"/> and
/// <see cref="RunValueTask(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/> run an asynchronous operation,
/// and retry it when it throws an exception or returns a result that <see cref="CanRetryResult"/> rejects. A
/// <see cref="Fallback"/> can give a value when the retries fail. They use every setting of the factory. When the
/// strategy or the policy refuses to retry an exception, they rethrow the exception of the last attempt with its
/// original stack trace. Unlike <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>, they do not queue the operation
/// to the thread pool: the first attempt runs on the caller's thread until its first <see langword="await"/>.
/// </item>
/// <item>
/// <c>StartNew</c> and its overloads start a task that runs a synchronous function on a scheduler, as
/// <see cref="TaskFactory{TResult}.StartNew(Func{TResult})"/> does, and retry it when it fails. An overload without a
/// strategy or a policy parameter uses the <see cref="Strategy"/> or the <see cref="Policy"/> of the factory. The task
/// faults with an <see cref="AggregateException"/> of the failures, as the remarks below describe. <c>StartNew</c>
/// cannot use <see cref="AttemptTimeout"/>, <see cref="TotalTimeout"/>, <see cref="OnRetry"/>,
/// <see cref="CircuitBreaker"/>, <see cref="CanRetryResult"/> or <see cref="Fallback"/>. When the factory has one of
/// these settings, each <c>StartNew</c> overload throws an <see cref="InvalidOperationException"/>, and does not run
/// the function.
/// </item>
/// </list>
/// </para>
/// <para>
/// The rest of these remarks describe <c>StartNew</c>, except the remarks about task types, which also apply to
/// <c>Run</c> and <c>RunValueTask</c>.
/// </para>
/// <para>
/// There are four ways to retry an operation in this library.
/// <list type="number">
/// <item><see cref="Retriable"/> puts retries around one call to a synchronous or an asynchronous operation.</item>
/// <item><see cref="RetryTask"/>, <see cref="RetryTask{T}"/> and a configured <see cref="RetryTaskFactory"/> or <see cref="RetryTaskFactory{T}"/> run an asynchronous operation with <c>Run</c>, or start a synchronous function as a task with <c>StartNew</c>. A factory adds timeouts, result retries, a circuit breaker and a fallback.</item>
/// <item><see cref="HedgedTask"/>, <see cref="HedgedTask{T}"/>, <see cref="HedgedTaskFactory"/> and <see cref="HedgedTaskFactory{T}"/> start more attempts in parallel when an attempt is slow or fails.</item>
/// <item><see cref="ReliableTaskRunner"/> runs a long-running asynchronous operation, and starts it again when it fails. An <see cref="IRetryPolicy"/> controls the restarts.</item>
/// </list>
/// </para>
/// <para>
/// After a try fails, the factory first gives the strategy a <see cref="RetryAttempt"/>. Its
/// <see cref="RetryAttempt.AttemptNumber"/> is the number of tries that have failed, starting at 1. Its
/// <see cref="RetryAttempt.PreviousDelay"/> is the delay before the try that failed, or <see cref="TimeSpan.Zero"/>
/// for the first try. Its <see cref="RetryAttempt.Exception"/> is the <see cref="AggregateException"/> of the task of
/// the failed try. If the strategy returns <see langword="null"/>, the function is not retried. Otherwise the policy
/// is asked about the exceptions that the try actually threw: the exceptions in that <see cref="AggregateException"/>,
/// and in any <see cref="AggregateException"/> that the function threw. The function is retried only if the policy
/// accepts all of them.
/// </para>
/// <para>
/// A try that cancels itself with an <see cref="OperationCanceledException"/> for a token other than the caller's has
/// failed: the strategy is given the <see cref="AggregateException"/> that contains it, and the policy is asked about
/// it. A try that acknowledges cancellation of the caller's token cancels the returned task, and the strategy and the
/// policy are not asked. If the strategy or the policy throws, the returned task faults with that exception, or is
/// cancelled if it is an <see cref="OperationCanceledException"/> and cancellation of the caller's token was requested.
/// The function is not retried.
/// </para>
/// <para>
/// When the function is not retried, the task faults with an <see cref="AggregateException"/> of the failures. Each
/// failure is the exception that a try threw. An <see cref="AggregateException"/> that the function threw is one
/// failure: it is not flattened. The task keeps at most 100 failures: the first 99 and the last.
/// </para>
/// <para>
/// Every try is queued to the same scheduler: the <c>scheduler</c> argument, or the <see cref="TaskScheduler.Current"/>
/// of the caller of <c>StartNew</c>. If the scheduler refuses to queue the first try, <c>StartNew</c> throws the
/// <see cref="TaskSchedulerException"/>, and the function does not run. If it refuses a retry, that try fails with the
/// <see cref="TaskSchedulerException"/>, and the strategy and the policy decide whether to retry, as for any other
/// failure. The strategy and the policy run on the thread pool, not on that scheduler.
/// </para>
/// <para>
/// <typeparamref name="T"/> cannot be <see cref="Task"/>, <see cref="Task{TResult}"/>, <see cref="ValueTask"/> or
/// <see cref="ValueTask{TResult}"/>. The task of a try completes when the function returns its task, before the
/// asynchronous operation ends, so a failure of the operation is not retried. For such a type, each
/// <c>StartNew</c> overload, <c>Run</c> and <c>RunValueTask</c> throw a <see cref="NotSupportedException"/> and do not
/// run the function. Use the type of the result of the task as <typeparamref name="T"/>, and call <c>Run</c> or
/// <c>RunValueTask</c>; or use <see cref="Retriable.RetryAsync{T}(Func{Task{T}})"/>.
/// </para>
/// <para>
/// When <typeparamref name="T"/> is not a task type, but the function returns a task as a value of a wider type (for
/// example a <see cref="Task"/> as an <see cref="object"/>, <see cref="IAsyncResult"/> or <see cref="IDisposable"/>,
/// or a boxed <see cref="ValueTask"/>), <c>StartNew</c> cannot refuse the call before the function runs. Instead, the
/// result of each successful try is checked: if it is a task, the returned task faults with a
/// <see cref="NotSupportedException"/>, and the function is not retried.
/// </para>
/// </remarks>
public sealed class RetryTaskFactory<T>
{
    private const string TaskResultMessage = "RetryTask<T> cannot retry a function that returns a task: the task completes when the function returns, before the operation ends. Use the result type of the task as T with Run or RunValueTask, or use Retriable.RetryAsync.";

    // A try of a function that returns a task completes before the asynchronous operation ends, so it is not retried.
    private static readonly bool ResultIsTask =
        typeof(Task).IsAssignableFrom(typeof(T)) ||
        typeof(T) == typeof(ValueTask) ||
        (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ValueTask<>));

    // Only object, or an interface that a task type implements (such as IAsyncResult), can hold a task when T is not
    // a task type. For any other T, the result is not checked, so that the check costs nothing.
    private static readonly bool ResultMayBeTask = !ResultIsTask && (typeof(T) == typeof(object) || typeof(T).IsInterface);

    private readonly IRetryStrategy strategy = RetryTaskFactory.DefaultStrategy;
    private readonly IRetryPolicy policy = AnyExceptionPolicy.Default;
    private readonly TimeSpan? attemptTimeout;
    private readonly TimeSpan? totalTimeout;
    private readonly TimeProvider? timeProvider;

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTaskFactory{T}"/> class, with the default settings.
    /// </summary>
    /// <remarks>
    /// The factory makes up to 5 tries, without delay, and retries any exception: its <see cref="Strategy"/> is
    /// <c>new Count()</c> and its <see cref="Policy"/> is <see cref="AnyExceptionPolicy.Default"/>. It has no timeouts,
    /// no callback, no circuit breaker, no result predicate and no fallback.
    /// </remarks>
    public RetryTaskFactory()
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="RetryTaskFactory{T}"/> class, with a strategy and a policy.
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
    /// Initialises a new instance of the <see cref="RetryTaskFactory{T}"/> class, with the settings of another
    /// factory.
    /// </summary>
    /// <param name="other">The factory whose settings to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Use this constructor with an object initialiser to make a copy that differs in some settings:
    /// <c>new RetryTaskFactory&lt;T&gt;(factory) { AttemptTimeout = TimeSpan.FromSeconds(5) }</c>. The copy shares the
    /// strategy, the policy, the callback, the circuit breaker, the result predicate and the fallback of
    /// <paramref name="other"/>.
    /// </remarks>
    public RetryTaskFactory(RetryTaskFactory<T> other)
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
        this.CanRetryResult = other.CanRetryResult;
        this.Fallback = other.Fallback;
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
        init => this.attemptTimeout = RetryTaskFactory.ValidateTimeout(value, nameof(this.AttemptTimeout));
    }

    /// <summary>
    /// Gets the time limit for all the attempts and all the delays, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the limit expires, the factory cancels the token of the attempt, or it stops the delay. No other attempt
    /// starts, and the call throws a <see cref="RetryTimeoutException"/>. The fallback can apply to that exception.
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
        init => this.totalTimeout = RetryTaskFactory.ValidateTimeout(value, nameof(this.TotalTimeout));
    }

    /// <summary>
    /// Gets a callback that the factory awaits before each delay, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives the number of the attempt that failed, and the exception or the result that caused the
    /// retry. The delay starts only when the callback completes. An exception from the callback stops the call.
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
    /// attempt timeout as a failure. A result that <see cref="CanRetryResult"/> rejects is a failure. Many factories
    /// can share one breaker.
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
    /// Gets a predicate that returns <see langword="true"/> for a result that is a failure and can be retried, or
    /// <see langword="null"/> if no result is a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory uses a result that the predicate rejects as a failure: the strategy, the callback and the delay
    /// apply, but the policy does not (the policy applies only to exceptions). An exception from the predicate stops
    /// the call.
    /// </para>
    /// <para>
    /// Before the next attempt, the factory disposes a rejected result that implements <see cref="IDisposable"/>. It
    /// does this after the callback. When no retries remain, the factory returns the last result, and does not
    /// dispose it.
    /// </para>
    /// <para>
    /// An exception from that disposal stops the call: the caller gets that exception, the next attempt does not
    /// start, and the fallback does not apply, because the exception is not an outcome of the attempts.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    public Func<T, bool>? CanRetryResult { get; init; }

    /// <summary>
    /// Gets the fallback that gives a value when the attempts fail, or <see langword="null"/> for no fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback is the outermost part of a call. It receives the exception or the last result after the retries,
    /// the total timeout and the circuit breaker. It never applies when the caller cancels the token.
    /// </para>
    /// <para>Only <c>Run</c> and <c>RunValueTask</c> can use this setting.</para>
    /// </remarks>
    public Fallback<T>? Fallback { get; init; }

    /// <summary>
    /// Runs an operation, and retries it when it throws an exception or returns a result that is a failure.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    /// The result of the first attempt that succeeds, or the last result if it is a failure and no retries remain. When
    /// the fallback applies, the value of its <see cref="Fallback{T}.Action"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses to retry an exception, the exception of the last attempt is rethrown
    /// with its original stack trace. When the strategy refuses to retry a result, the call returns that result.
    /// </para>
    /// <para>
    /// When <see cref="Fallback"/> is set, the fallback receives that exception or that result. It does not receive a
    /// cancellation that the caller caused. It does not receive an exception from a callback, the strategy or a policy
    /// of the factory: such an exception stops the call. An exception from the fallback stops the call.
    /// </para>
    /// <para>
    /// Unlike <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>, this method does not queue the operation to the
    /// thread pool: the first attempt runs on the caller's thread until its first <see langword="await"/>.
    /// </para>
    /// </remarks>
    public Task<T> Run(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfResultIsTask();

        // The fallback handles only an exception that the engine marks as the outcome of the attempts, so only a call
        // with a fallback needs the marker.
        CallOutcome? outcome = this.Fallback is null ? null : new CallOutcome();
        ValueTask<T> core = RetryEngine.RunAsync<RetryOperations.ResultCheckedTaskOperation<T>, T>(
            new(operation, this.CanRetryResult),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext,
            outcome);

        // Without a fallback, the call has no extra state machine.
        return (this.Fallback is null ? core : FallbackEngine.WithFallbackAsync(core, this.Fallback, outcome!, cancellationToken, this.ContinueOnCapturedContext)).AsTask();
    }

    /// <summary>
    /// Runs an operation that returns a <see cref="ValueTask{TResult}"/>, and retries it when it throws an exception or
    /// returns a result that is a failure.
    /// </summary>
    /// <param name="operation">
    /// The operation. It receives a token that is cancelled when the caller cancels or when a timeout expires.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    /// The result of the first attempt that succeeds, or the last result if it is a failure and no retries remain. When
    /// the fallback applies, the value of its <see cref="Fallback{T}.Action"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <remarks>
    /// This method behaves as <see cref="Run(Func{CancellationToken, Task{T}}, CancellationToken)"/>. When there is no
    /// timeout, no callback and no fallback, and every attempt completes synchronously, a call allocates nothing.
    /// </remarks>
    public ValueTask<T> RunValueTask(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfResultIsTask();

        CallOutcome? outcome = this.Fallback is null ? null : new CallOutcome();
        ValueTask<T> core = RetryEngine.RunAsync<RetryOperations.ResultCheckedOperation<T>, T>(
            new(operation, this.CanRetryResult),
            cancellationToken,
            this.strategy,
            this.policy,
            this.TimeProvider,
            this.attemptTimeout,
            this.totalTimeout,
            this.OnRetry,
            this.CircuitBreaker,
            this.ContinueOnCapturedContext,
            outcome);

        return this.Fallback is null ? core : FallbackEngine.WithFallbackAsync(core, this.Fallback, outcome!, cancellationToken, this.ContinueOnCapturedContext);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, cancellationToken, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, taskCreationOptions, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, taskCreationOptions, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(function);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, this.Strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, cancellationToken, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, taskCreationOptions, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, taskCreationOptions, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, strategy, this.Policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, cancellationToken, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, taskCreationOptions, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, taskCreationOptions, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, state, cancellationToken, taskCreationOptions, scheduler, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(policy);

        return this.StartNew(function, cancellationToken, taskCreationOptions, scheduler, this.Strategy, policy);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function), () => new Task<T>(function), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken), () => new Task<T>(function, cancellationToken), GetResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state), () => new Task<T>(function, state), GetResult, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, TaskCreationOptions taskCreationOptions, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, RetryTaskFactory.Detached(taskCreationOptions)), () => new Task<T>(function, state, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, CancellationToken.None, taskCreationOptions, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, cancellationToken), () => new Task<T>(function, state, cancellationToken), GetResult, cancellationToken, TaskCreationOptions.None, TaskScheduler.Current, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="state">The state to pass to the task on creation.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<object?, T> function, object? state, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, state, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy, this.TimeProvider);
    }

    /// <summary>
    /// Start a new retriable task for a function.
    /// </summary>
    /// <param name="function">The function for which to start a retriable task.</param>
    /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
    /// <param name="taskCreationOptions">The task creation options.</param>
    /// <param name="scheduler">The task scheduler.</param>
    /// <param name="strategy">
    /// The retry strategy. The remarks on <see cref="RetryTaskFactory{T}"/> describe the <see cref="RetryAttempt"/> it is given.
    /// </param>
    /// <param name="policy">The retry policy.</param>
    /// <returns>A task which completes when the function is complete.</returns>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> is a task type. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="TaskSchedulerException">
    /// The scheduler refused to queue the first try. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The factory has a setting that <c>StartNew</c> cannot use. See the remarks on <see cref="RetryTaskFactory{T}"/>.
    /// </exception>
    public Task<T> StartNew(Func<T> function, CancellationToken cancellationToken, TaskCreationOptions taskCreationOptions, TaskScheduler scheduler, IRetryStrategy strategy, IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policy);

        ThrowIfResultIsTask();
        this.ThrowIfStartNewCannotHonourSettings();

        return RetryTaskFactory.Start(Task<T>.Factory.StartNew(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions), scheduler), () => new Task<T>(function, cancellationToken, RetryTaskFactory.Detached(taskCreationOptions)), GetResult, cancellationToken, taskCreationOptions, scheduler, strategy, policy, this.TimeProvider);
    }

    private static T GetResult(Task task)
    {
        T result = ((Task<T>)task).Result;

        // An exception here faults the operation's task, and is not retried.
        if (ResultMayBeTask && IsTask(result))
        {
            throw new NotSupportedException(TaskResultMessage);
        }

        return result;
    }

    private static bool IsTask(T result)
    {
        return result is Task or ValueTask
            || (result is not null && result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>));
    }

    private static void ThrowIfResultIsTask()
    {
        if (ResultIsTask)
        {
            throw new NotSupportedException(TaskResultMessage);
        }
    }

    private void ThrowIfStartNewCannotHonourSettings()
    {
        RetryTaskFactory.ThrowIfStartNewCannotHonour(this.attemptTimeout, this.totalTimeout, this.OnRetry, this.CircuitBreaker, this.CanRetryResult, this.Fallback);
    }
}