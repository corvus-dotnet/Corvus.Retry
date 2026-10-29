// <copyright file="HedgingEngine.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// The engine that runs hedged attempts: it starts more attempts in parallel when an attempt is slow or fails, and the
/// first acceptable outcome wins.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt has its own cancellation source. The source is linked to the outer token (the caller's token, or the
/// source of the total timeout), it has the attempt timeout, and the engine cancels it when the attempt can no longer
/// win. The engine does not wait for the attempts that it cancels.
/// </para>
/// <para>
/// An attempt never throws to the engine. It records its outcome, and the engine reads the outcome only after the
/// task of the attempt completes. When the engine stops before an attempt completes, the attempt owns its outcome: it
/// disposes its result, and the engine never sees its exception. A compare-and-swap on the state of the attempt
/// decides which side owns the outcome. The cancellation source of an attempt has two references, one for the engine
/// and one for the attempt, and the last to release it disposes it.
/// </para>
/// <para>
/// The attempt and the engine share the circuit breaker's permit of an attempt. Whichever of them comes first reports
/// the call to the breaker, exactly once: the attempt when it has an outcome, or the engine when it abandons the
/// attempt. The engine releases the permit of an abandoned attempt before the call returns or throws, so that a caller
/// who retries at once can make the test call of a half-open circuit. When the total timeout stops the call, the engine
/// reports each attempt in flight as a failure.
/// </para>
/// <para>
/// The first attempt runs on the caller's thread until its first await. Each later attempt starts on the thread pool,
/// so an attempt that blocks the thread does not stop the engine from starting the next attempt.
/// </para>
/// <para>
/// Hedging allocates: a task and a cancellation source for each attempt, a timer for each delay, and the state of the
/// call. Each attempt after the first also allocates the tasks that start it on the thread pool.
/// </para>
/// </remarks>
internal static class HedgingEngine
{
    private static readonly Action<object?> CancelSource = static s => ((CancellationTokenSource)s!).Cancel();
    private static readonly Action<object?> SetSignal = static s => ((TaskCompletionSource)s!).TrySetResult();
    private static readonly TimerCallback SetSignalOnTimer = static s => ((TaskCompletionSource)s!).TrySetResult();

    /// <summary>
    /// The outcome of an attempt.
    /// </summary>
    private enum AttemptOutcome
    {
        /// <summary>The attempt has not completed.</summary>
        None,

        /// <summary>The attempt returned a result that is not a failure. It wins.</summary>
        Succeeded,

        /// <summary>The attempt returned a result that is a failure.</summary>
        Rejected,

        /// <summary>The attempt threw an exception, timed out, or the circuit breaker rejected it.</summary>
        Failed,

        /// <summary>The caller cancelled the attempt. This stops the call.</summary>
        Cancelled,

        /// <summary>The total timeout expired during the attempt. This stops the call.</summary>
        TotalTimeout,

        /// <summary>A callback, the result predicate or the circuit breaker threw an exception. This stops the call.</summary>
        Stopped,
    }

    /// <summary>
    /// Runs an operation with hedging.
    /// </summary>
    /// <typeparam name="T">The type of the result of the operation.</typeparam>
    /// <param name="primary">The primary operation.</param>
    /// <param name="generator">
    /// A function that gives the operation for each attempt, or <see langword="null"/>. When it returns
    /// <see langword="null"/>, the attempt uses <paramref name="primary"/>.
    /// </param>
    /// <param name="isFailure">A predicate that returns <see langword="true"/> for a result that is a failure, or <see langword="null"/>.</param>
    /// <param name="hedging">The settings of hedging. The pipeline has checked them.</param>
    /// <param name="caller">The caller's token. Its cancellation stops the call with an <see cref="OperationCanceledException"/>.</param>
    /// <param name="timeProvider">The time provider for the hedging delay and the timeouts.</param>
    /// <param name="attemptTimeout">The time limit for each attempt, or <see langword="null"/> for no limit.</param>
    /// <param name="totalTimeout">The time limit for all the attempts, or <see langword="null"/> for no limit.</param>
    /// <param name="circuitBreaker">The circuit breaker that permits each attempt and counts its outcome, or <see langword="null"/>.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context after each await.</param>
    /// <param name="outcome">
    /// Receives the exception that the call throws as the outcome of its attempts, or <see langword="null"/>. The engine
    /// does not set it for an exception from a callback, the result predicate, a policy, or the circuit breaker.
    /// </param>
    /// <returns>
    /// The result of the first attempt whose outcome is not a failure, or the last result if every attempt failed and
    /// the last outcome was a result.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The caller's token was cancelled. The exception is for <paramref name="caller"/>, whether the engine or an attempt
    /// sees the cancellation first.
    /// </exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired. The inner exception is the exception of the last attempt that failed, or
    /// <see langword="null"/> if no attempt has failed or the last outcome was a result that is a failure. It does not
    /// depend on whether the engine or an attempt sees the timeout first.
    /// </exception>
    /// <remarks>
    /// When every attempt fails and the last outcome is an exception, that exception is rethrown with its original stack
    /// trace. An exception that the policy of <paramref name="hedging"/> refuses is not a failure: it is rethrown at
    /// once. The policy is asked about the exceptions that the operation threw: for an <see cref="AggregateException"/>,
    /// about each inner exception. An exception from a callback stops the call.
    /// </remarks>
    internal static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> primary,
        Func<int, Func<CancellationToken, ValueTask<T>>?>? generator,
        Func<T, bool>? isFailure,
        HedgingOptions hedging,
        CancellationToken caller,
        TimeProvider timeProvider,
        TimeSpan? attemptTimeout,
        TimeSpan? totalTimeout,
        CircuitBreaker? circuitBreaker,
        bool continueOnCapturedContext,
        CallOutcome? outcome = null)
    {
        // The total timeout cancels the token that every attempt uses. The caller's token cancels it too.
        CancellationTokenSource? totalCts = null;
        CancellationTokenRegistration totalRegistration = default;
        CancellationToken outer = caller;
        TimeSpan total = totalTimeout.GetValueOrDefault();
        if (totalTimeout.HasValue)
        {
            totalCts = new CancellationTokenSource(total, timeProvider);
            totalRegistration = caller.UnsafeRegister(CancelSource, totalCts);
            outer = totalCts.Token;
        }

        HedgedCall<T> call = new(
            primary,
            generator,
            isFailure,
            hedging.MaxHedgedAttempts + 1,
            caller,
            outer,
            totalCts,
            total,
            attemptTimeout,
            circuitBreaker,
            timeProvider,
            continueOnCapturedContext,
            outcome);
        Func<HedgingEventArgs, ValueTask>? onHedging = hedging.OnHedging;
        TimeSpan delay = hedging.Delay;

        // A zero delay starts the attempts one after the other until an outcome wins, and an infinite delay starts an
        // attempt only after a failure.
        bool useDelay = delay > TimeSpan.Zero;
        bool restartDelay = false;
        ITimer? delayTimer = null;
        TaskCompletionSource? delaySignal = null;

        // This wakes the engine when the outer token is cancelled, so that the call does not wait for an attempt that
        // ignores its token.
        TaskCompletionSource? cancelSignal = null;
        CancellationTokenRegistration cancelRegistration = default;

        T lastResult = default!;
        bool holdsResult = false;

        try
        {
            call.StartNext();
            restartDelay = useDelay;

            while (true)
            {
                // An outcome that wins comes first. Read the other outcomes in the sequence in which the attempts started.
                HedgedAttempt<T>? completed = call.TakeCompleted();
                if (completed is null)
                {
                    if (call.CanStartNext && (delay == TimeSpan.Zero || delaySignal is { Task.IsCompleted: true }))
                    {
                        // The delay expired, or is zero, before an outcome won.
                        StopDelay();
                        await BeforeHedgedAttemptAsync(null).ConfigureAwait(continueOnCapturedContext);
                        StartNextUnlessAnAttemptWon();
                        restartDelay = true;
                        continue;
                    }

                    if (outer.IsCancellationRequested)
                    {
                        // Only the caller's token and the total timeout can cancel the outer token.
                        caller.ThrowIfCancellationRequested();
                        throw call.TotalTimeoutExpired();
                    }

                    // Make the timer and the cancellation signal only when the engine must wait, so that a call whose attempts
                    // complete synchronously needs neither. Only synchronous work runs between the start of the last
                    // attempt and this point, so the delay still starts with the attempt.
                    if (restartDelay)
                    {
                        restartDelay = false;
                        StartDelay();
                    }

                    if (cancelSignal is null && outer.CanBeCanceled)
                    {
                        cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        cancelRegistration = outer.UnsafeRegister(SetSignal, cancelSignal);
                    }

                    await Task.WhenAny(call.GetTasksToWaitFor(delaySignal?.Task, cancelSignal?.Task)).ConfigureAwait(continueOnCapturedContext);
                    continue;
                }

                switch (completed.Outcome)
                {
                    case AttemptOutcome.Succeeded:
                        return completed.Result;

                    case AttemptOutcome.Rejected:
                        // A result is now the last outcome, so a total timeout from now on has no inner exception.
                        call.LastFailure = null;
                        if (call.CanStartNext)
                        {
                            StopDelay();
                            try
                            {
                                await BeforeHedgedAttemptAsync(null).ConfigureAwait(continueOnCapturedContext);
                            }
                            finally
                            {
                                // The result is not returned to the caller, so release it after the callback.
                                (completed.Result as IDisposable)?.Dispose();
                            }

                            StartNextUnlessAnAttemptWon();
                            restartDelay = true;
                        }
                        else
                        {
                            // Keep the result: if it is the last outcome, the caller gets it.
                            ReleaseLastResult();
                            lastResult = completed.Result;
                            holdsResult = true;
                        }

                        break;

                    case AttemptOutcome.Failed:
                        Exception failure = completed.Exception!;
                        if (!CanRetry(hedging.Policy, failure))
                        {
                            // An exception that the policy refuses is not a failure, so it wins.
                            ExceptionDispatchInfo.Throw(SetOutcome(outcome, failure));
                        }

                        call.LastFailure = failure;

                        if (call.CanStartNext)
                        {
                            StopDelay();
                            await BeforeHedgedAttemptAsync(failure).ConfigureAwait(continueOnCapturedContext);
                            StartNextUnlessAnAttemptWon();
                            restartDelay = true;
                        }
                        else
                        {
                            ReleaseLastResult();
                        }

                        break;

                    case AttemptOutcome.Cancelled:
                        // Only the caller's token cancels an attempt whose outcome the engine reads. Throw for the
                        // caller's token, as when the engine sees the cancellation first, so that the exception does not
                        // depend on which side sees it first.
                        caller.ThrowIfCancellationRequested();
                        ExceptionDispatchInfo.Throw(completed.Exception!);
                        break;

                    case AttemptOutcome.TotalTimeout:
                        // The same exception as when the engine sees the timeout first.
                        throw call.TotalTimeoutExpired();

                    default:
                        // An exception from a callback stops the call. Rethrow it with its original stack trace. It is not
                        // an outcome of the attempts.
                        ExceptionDispatchInfo.Throw(completed.Exception!);
                        break;
                }

                if (!call.HasPendingAttempts)
                {
                    // Every attempt has started, and every attempt failed. The caller gets the last outcome.
                    if (holdsResult)
                    {
                        holdsResult = false;
                        return lastResult;
                    }

                    ExceptionDispatchInfo.Throw(SetOutcome(outcome, call.LastFailure!));
                }
            }
        }
        catch (RetryTimeoutException timeout) when (ReferenceEquals(timeout, call.TotalTimeoutException))
        {
            // The total timeout cut short each attempt in flight, so each is a failed call for the circuit breaker. Report
            // them before the call throws: an attempt whose operation ignores its token might never report.
            await call.ReportTotalTimeoutAsync(timeout).ConfigureAwait(continueOnCapturedContext);
            throw;
        }
        finally
        {
            StopDelay();
            cancelRegistration.Dispose();

            // Cancel the attempts that have not won, and do not wait for them. This releases the breaker's permit of each
            // of them before the call returns or throws.
            call.AbandonPendingAttempts();

            if (holdsResult)
            {
                DisposeQuietly(lastResult);
            }

            totalRegistration.Dispose();
            totalCts?.Dispose();
        }

        async ValueTask BeforeHedgedAttemptAsync(Exception? exception)
        {
            // Do not report an attempt that will not run.
            call.ThrowIfStopped();
            if (onHedging is not null)
            {
                await onHedging(new HedgingEventArgs(call.NextAttemptNumber, exception)).ConfigureAwait(continueOnCapturedContext);
            }
        }

        void StartNextUnlessAnAttemptWon()
        {
            // An attempt can win while an outcome is read or while the callback runs. The engine reads that outcome
            // next, so do not start another attempt.
            if (!call.HasCompletedWinner)
            {
                call.StartNext();
            }
        }

        void StartDelay()
        {
            // The delay starts again each time an attempt starts.
            if (useDelay && call.CanStartNext)
            {
                delaySignal = new TaskCompletionSource();
                delayTimer = timeProvider.CreateTimer(SetSignalOnTimer, delaySignal, delay, Timeout.InfiniteTimeSpan);
            }
        }

        void StopDelay()
        {
            // A timer that fires after this completes a signal that the engine no longer reads.
            delayTimer?.Dispose();
            delayTimer = null;
            delaySignal = null;
        }

        void ReleaseLastResult()
        {
            if (holdsResult)
            {
                holdsResult = false;
                (lastResult as IDisposable)?.Dispose();
                lastResult = default!;
            }
        }
    }

    // Asks the policy about the exceptions that the operation threw, not the AggregateException around them.
    private static bool CanRetry(IRetryPolicy policy, Exception failure)
    {
        return failure is AggregateException aggregate ? policy.CanRetryAll(aggregate) : policy.CanRetry(failure);
    }

    // Records an exception that the call throws as the outcome of its attempts, so that the fallback can tell it from an
    // exception that a callback, the result predicate or a policy throws.
    private static Exception SetOutcome(CallOutcome? outcome, Exception exception)
    {
        if (outcome is not null)
        {
            outcome.Exception = exception;
        }

        return exception;
    }

    [SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "The result belongs to an attempt that lost, or to a call that already has an outcome. No caller can receive the exception.")]
    private static void DisposeQuietly<T>(T result)
    {
        if (result is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // The result is not returned, and the call already has an outcome, so the exception has no receiver.
            }
        }
    }

    /// <summary>
    /// The state of one hedged call: the settings, and the attempts that it started.
    /// </summary>
    /// <typeparam name="T">The type of the result of the operation.</typeparam>
    private sealed class HedgedCall<T>
    {
        private readonly Func<CancellationToken, ValueTask<T>> primary;
        private readonly Func<int, Func<CancellationToken, ValueTask<T>>?>? generator;
        private readonly HedgedAttempt<T>?[] attempts;
        private readonly List<HedgedAttempt<T>> pending;
        private int started;

        public HedgedCall(
            Func<CancellationToken, ValueTask<T>> primary,
            Func<int, Func<CancellationToken, ValueTask<T>>?>? generator,
            Func<T, bool>? isFailure,
            int maxAttempts,
            CancellationToken caller,
            CancellationToken outer,
            CancellationTokenSource? totalCts,
            TimeSpan total,
            TimeSpan? attemptTimeout,
            CircuitBreaker? circuitBreaker,
            TimeProvider timeProvider,
            bool continueOnCapturedContext,
            CallOutcome? outcome)
        {
            this.primary = primary;
            this.generator = generator;
            this.IsFailure = isFailure;
            this.Caller = caller;
            this.Outer = outer;
            this.TotalCts = totalCts;
            this.Total = total;
            this.AttemptTimeout = attemptTimeout;
            this.CircuitBreaker = circuitBreaker;
            this.TimeProvider = timeProvider;
            this.ContinueOnCapturedContext = continueOnCapturedContext;
            this.CallOutcome = outcome;
            this.attempts = new HedgedAttempt<T>?[maxAttempts];
            this.pending = new List<HedgedAttempt<T>>(maxAttempts);
        }

        public Func<T, bool>? IsFailure { get; }

        public CancellationToken Caller { get; }

        public CancellationToken Outer { get; }

        public CancellationTokenSource? TotalCts { get; }

        public TimeSpan Total { get; }

        public TimeSpan? AttemptTimeout { get; }

        public CircuitBreaker? CircuitBreaker { get; }

        public TimeProvider TimeProvider { get; }

        public bool ContinueOnCapturedContext { get; }

        public CallOutcome? CallOutcome { get; }

        /// <summary>
        /// Gets or sets the exception of the last attempt whose outcome the engine read, or <see langword="null"/> if that
        /// outcome was a result, or if the engine has read no outcome.
        /// </summary>
        public Exception? LastFailure { get; set; }

        /// <summary>
        /// Gets the exception that the engine made when the total timeout stopped the call, or <see langword="null"/>.
        /// </summary>
        public RetryTimeoutException? TotalTimeoutException { get; private set; }

        public bool CanStartNext => this.started < this.attempts.Length;

        public int NextAttemptNumber => this.started + 1;

        public bool HasPendingAttempts => this.pending.Count > 0;

        /// <summary>
        /// Gets a value indicating whether an attempt that the engine has not read has an outcome that wins or stops the
        /// call.
        /// </summary>
        public bool HasCompletedWinner => this.IndexOfCompletedWinner() >= 0;

        public void StartNext()
        {
            // Do not start an attempt that cannot run.
            this.ThrowIfStopped();

            int number = this.started + 1;
            Func<CancellationToken, ValueTask<T>> operation = this.generator?.Invoke(number) ?? this.primary;

            // Record the attempt before it starts, so that the engine releases it whatever happens next.
            HedgedAttempt<T> attempt = new(this);
            this.attempts[this.started++] = attempt;
            this.pending.Add(attempt);

            // The first attempt runs on the caller's thread until its first await. Each later attempt starts on the
            // thread pool, so that an attempt that blocks before its first await cannot delay the next attempt.
            attempt.Start(operation, inline: number == 1);
        }

        /// <summary>
        /// Removes and returns a completed attempt. An attempt whose outcome wins or stops the call comes first. The other
        /// attempts come in the sequence in which they started.
        /// </summary>
        /// <returns>A completed attempt, or <see langword="null"/> if no attempt has completed.</returns>
        public HedgedAttempt<T>? TakeCompleted()
        {
            int index = this.IndexOfCompletedWinner();
            if (index < 0)
            {
                index = this.pending.FindIndex(static attempt => attempt.Task.IsCompleted);
                if (index < 0)
                {
                    return null;
                }
            }

            HedgedAttempt<T> completed = this.pending[index];
            this.pending.RemoveAt(index);
            return completed;
        }

        /// <summary>
        /// Throws if the caller cancelled the call or the total timeout expired.
        /// </summary>
        public void ThrowIfStopped()
        {
            this.Caller.ThrowIfCancellationRequested();
            if (this.TotalCts is { IsCancellationRequested: true })
            {
                throw this.TotalTimeoutExpired();
            }
        }

        /// <summary>
        /// Makes the exception for a total timeout that stops the call, and records it as the outcome of the call.
        /// </summary>
        /// <returns>The exception. Its inner exception is <see cref="LastFailure"/>.</returns>
        public RetryTimeoutException TotalTimeoutExpired()
        {
            this.TotalTimeoutException = new RetryTimeoutException(this.Total, this.LastFailure);
            SetOutcome(this.CallOutcome, this.TotalTimeoutException);
            return this.TotalTimeoutException;
        }

        /// <summary>
        /// Reports to the circuit breaker each attempt in flight that the total timeout cut short as a failed call, unless
        /// the attempt reported its outcome first.
        /// </summary>
        /// <param name="timeout">The exception of the total timeout.</param>
        /// <returns>A task that completes when the breaker has called its callback for each change of state.</returns>
        public async ValueTask ReportTotalTimeoutAsync(RetryTimeoutException timeout)
        {
            if (this.CircuitBreaker is not CircuitBreaker breaker)
            {
                return;
            }

            foreach (HedgedAttempt<T> attempt in this.pending)
            {
                if (attempt.TryTakePermitToReport(out int probe))
                {
                    await breaker.NotifyAsync(breaker.OnFailure(timeout, probe)).ConfigureAwait(this.ContinueOnCapturedContext);
                }
            }
        }

        public Task[] GetTasksToWaitFor(Task? delay, Task? cancellation)
        {
            Task[] tasks = new Task[this.pending.Count + (delay is null ? 0 : 1) + (cancellation is null ? 0 : 1)];
            int count = 0;
            foreach (HedgedAttempt<T> attempt in this.pending)
            {
                tasks[count++] = attempt.Task;
            }

            if (delay is not null)
            {
                tasks[count++] = delay;
            }

            if (cancellation is not null)
            {
                tasks[count] = cancellation;
            }

            return tasks;
        }

        public void AbandonPendingAttempts()
        {
            foreach (HedgedAttempt<T> attempt in this.pending)
            {
                attempt.Abandon();
            }

            this.pending.Clear();

            foreach (HedgedAttempt<T>? attempt in this.attempts)
            {
                attempt?.Release();
            }
        }

        private int IndexOfCompletedWinner()
        {
            // The attempt records its outcome before its task completes, so the outcome of a completed task is final.
            return this.pending.FindIndex(
                static attempt => attempt.Task.IsCompleted
                    && attempt.Outcome is AttemptOutcome.Succeeded
                        or AttemptOutcome.Cancelled
                        or AttemptOutcome.TotalTimeout
                        or AttemptOutcome.Stopped);
        }
    }

    /// <summary>
    /// One hedged attempt.
    /// </summary>
    /// <typeparam name="T">The type of the result of the operation.</typeparam>
    private sealed class HedgedAttempt<T>
    {
        private const int Running = 0;
        private const int Completed = 1;
        private const int Abandoned = 2;

        // The states of the breaker's permit of the attempt.
        private const int NoPermit = 0;
        private const int PermitHeld = 1;
        private const int PermitReporting = 2;
        private const int PermitReleased = 3;

        private readonly HedgedCall<T> call;
        private readonly CancellationTokenSource cts;
        private readonly CancellationTokenRegistration registration;
        private Func<CancellationToken, ValueTask<T>>? operation;
        private Task? task;
        private bool hasResult;

        // Running until the attempt completes or the engine abandons it, whichever happens first.
        private int state;

        // One reference for the engine and one for the attempt. The last to release disposes the source.
        private int references = 2;

        // The breaker's permit of the attempt. The side that moves it from held to reporting reports the outcome of the
        // call, so the breaker hears about the call at most once. Either side can release a permit that nobody reports:
        // a release is idempotent, so both sides release it when it is released, which closes the window in which one side
        // has claimed the release and the other has returned.
        private int permit = NoPermit;
        private int probe;

        public HedgedAttempt(HedgedCall<T> call)
        {
            this.call = call;

            // The source also lets the engine cancel an attempt that can no longer win.
            this.cts = call.AttemptTimeout is TimeSpan timeout
                ? new CancellationTokenSource(timeout, call.TimeProvider)
                : new CancellationTokenSource();
            this.registration = call.Outer.UnsafeRegister(CancelSource, this.cts);
        }

        public Task Task => this.task!;

        public AttemptOutcome Outcome { get; private set; }

        public T Result { get; private set; } = default!;

        public Exception? Exception { get; private set; }

        /// <summary>
        /// Starts the attempt.
        /// </summary>
        /// <param name="operation">The operation of the attempt.</param>
        /// <param name="inline">
        /// <see langword="true"/> to run the operation on the current thread until its first await;
        /// <see langword="false"/> to start it on the thread pool.
        /// </param>
        public void Start(Func<CancellationToken, ValueTask<T>> operation, bool inline)
        {
            this.operation = operation;
            this.task = inline
                ? this.RunAsync()
                : Task.Factory.StartNew(
                    static s => ((HedgedAttempt<T>)s!).RunAsync(),
                    this,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
        }

        /// <summary>
        /// Stops the engine from reading the outcome of the attempt, and cancels the attempt if it is still running.
        /// </summary>
        public void Abandon()
        {
            if (Interlocked.CompareExchange(ref this.state, Abandoned, Running) == Running)
            {
                // An abandoned attempt is not a call. Release its permit before the cancellation, so that the attempt
                // cannot report a call that the engine abandoned, and so that the next call can make the test call before
                // this call returns. The engine does not wait for the operation.
                this.ReleasePermit();

                try
                {
                    this.cts.Cancel();
                }
                catch (AggregateException)
                {
                    // A callback on the token of the losing attempt threw. The call has an outcome, so the exception has
                    // no receiver.
                }
            }
            else
            {
                // The attempt completed, but the engine did not read its outcome.
                this.DisposeResult();
            }
        }

        /// <summary>
        /// Takes the breaker's permit of the attempt to report the outcome of the call, if the attempt holds a permit that
        /// neither side has reported or released.
        /// </summary>
        /// <param name="permitProbe">The number of the test call that the breaker gave, or 0.</param>
        /// <returns><see langword="true"/> if the caller must report the call; otherwise <see langword="false"/>.</returns>
        public bool TryTakePermitToReport(out int permitProbe)
        {
            if (Interlocked.CompareExchange(ref this.permit, PermitReporting, PermitHeld) == PermitHeld)
            {
                permitProbe = this.probe;
                return true;
            }

            permitProbe = 0;
            return false;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref this.references) == 0)
            {
                // Dispose the registration first, so that the outer token cannot cancel a disposed source.
                this.registration.Dispose();
                this.cts.Dispose();
            }
        }

        private async Task RunAsync()
        {
            Func<CancellationToken, ValueTask<T>> operation = this.operation!;
            HedgedCall<T> call = this.call;
            CircuitBreaker? breaker = call.CircuitBreaker;
            bool acquired = false;
            bool reporting = false;
            int probe = 0;
            bool reported = false;
            try
            {
                if (Volatile.Read(ref this.state) == Abandoned)
                {
                    // The call ended before the thread pool started the attempt, so do not run the operation.
                    this.Outcome = AttemptOutcome.Cancelled;
                    return;
                }

                // The circuit breaker is outside the attempt timeout.
                if (breaker is not null)
                {
                    acquired = breaker.TryAcquire(out probe, out BrokenCircuitException? rejection, out CircuitStateChange? change);
                    if (acquired)
                    {
                        // From now on the engine can take the permit. The exchange is a full fence, so the engine sees the
                        // permit, or this attempt sees below that the engine abandoned it.
                        this.probe = probe;
                        Interlocked.Exchange(ref this.permit, PermitHeld);
                    }

                    if (change.HasValue)
                    {
                        await breaker.NotifyAsync(change).ConfigureAwait(call.ContinueOnCapturedContext);
                    }

                    if (!acquired)
                    {
                        // A rejected attempt is a failure for the hedging policy, but it is not a call for the breaker.
                        this.Exception = rejection;
                        this.Outcome = AttemptOutcome.Failed;
                        return;
                    }

                    if (Volatile.Read(ref this.state) == Abandoned)
                    {
                        // The engine abandoned the attempt while it acquired the permit. Do not run the operation.
                        this.Outcome = AttemptOutcome.Cancelled;
                        return;
                    }
                }

                Exception? failure = null;
                try
                {
                    this.Result = await operation(this.cts.Token).ConfigureAwait(call.ContinueOnCapturedContext);
                    this.hasResult = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                CircuitStateChange? outcomeChange;
                if (failure is null)
                {
                    bool rejected = call.IsFailure is { } isFailure && isFailure(this.Result);
                    this.Outcome = rejected ? AttemptOutcome.Rejected : AttemptOutcome.Succeeded;
                    if (!acquired || !(reporting = this.TryTakePermitToReport(out _)))
                    {
                        // No breaker, or the engine abandoned the attempt and released the permit.
                        return;
                    }

                    outcomeChange = rejected ? breaker!.OnFailure(null, probe) : breaker!.OnSuccess(probe);
                }
                else
                {
                    this.Outcome = AttemptOutcome.Failed;
                    if (failure is OperationCanceledException cancellation)
                    {
                        switch (this.Classify())
                        {
                            case CancellationCause.Caller:
                                // A cancellation by the caller, or by the engine for an attempt that lost, is not a call.
                                this.Outcome = AttemptOutcome.Cancelled;
                                this.Exception = failure;
                                return;

                            case CancellationCause.TotalTimeout:
                                this.Outcome = AttemptOutcome.TotalTimeout;
                                failure = new RetryTimeoutException(call.Total, cancellation);
                                break;

                            case CancellationCause.AttemptTimeout:
                                failure = new RetryTimeoutException(call.AttemptTimeout.GetValueOrDefault(), cancellation);
                                break;

                            default:
                                // The operation cancelled itself. The policy decides, as for any exception.
                                break;
                        }
                    }

                    this.Exception = failure;
                    if (!acquired || !(reporting = this.TryTakePermitToReport(out _)))
                    {
                        // No breaker, or the engine released the permit, or reported the call for the total timeout.
                        return;
                    }

                    outcomeChange = breaker!.OnFailure(failure, probe);
                }

                reported = true;
                await breaker.NotifyAsync(outcomeChange).ConfigureAwait(call.ContinueOnCapturedContext);
            }
            catch (Exception exception)
            {
                // An exception from a callback, from the result predicate, or from the circuit breaker stops the call.
                // The caller does not get the result, so release it.
                this.DisposeResult();
                this.hasResult = false;
                this.Outcome = AttemptOutcome.Stopped;
                this.Exception = exception;
            }
            finally
            {
                if (acquired && !reported)
                {
                    // The attempt has no outcome for the breaker. If it was the test call, let another call make it.
                    if (reporting)
                    {
                        // The report threw, so the breaker has no outcome for the call.
                        breaker!.Abandon(probe);
                    }
                    else
                    {
                        this.ReleasePermit();
                    }
                }

                if (Interlocked.CompareExchange(ref this.state, Completed, Running) == Abandoned)
                {
                    // The engine no longer reads the outcome, so release the result here.
                    this.DisposeResult();
                }

                this.Release();
            }
        }

        private CancellationCause Classify()
        {
            CancellationCause cause = RetryEngine.Classify(this.call.Caller, this.call.TotalCts, this.cts);

            // The source of the attempt is cancelled by the attempt timeout, or by the engine when the attempt lost.
            if (cause == CancellationCause.AttemptTimeout
                && (this.call.AttemptTimeout is null || Volatile.Read(ref this.state) == Abandoned))
            {
                return CancellationCause.Caller;
            }

            return cause;
        }

        /// <summary>
        /// Releases the breaker's permit of the attempt without an outcome, unless a side is reporting the outcome.
        /// </summary>
        private void ReleasePermit()
        {
            int previous = Interlocked.CompareExchange(ref this.permit, PermitReleased, PermitHeld);
            if (previous is PermitHeld or PermitReleased)
            {
                // The other side may release it too. The second release does nothing.
                this.call.CircuitBreaker!.Abandon(this.probe);
            }
        }

        private void DisposeResult()
        {
            if (this.hasResult)
            {
                DisposeQuietly(this.Result);
            }
        }
    }
}
