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
/// Hedging allocates: a task and a cancellation source for each attempt, a timer for each delay, and the state of the
/// call.
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
    /// <returns>
    /// The result of the first attempt whose outcome is not a failure, or the last result if every attempt failed and
    /// the last outcome was a result.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">The total timeout expired.</exception>
    /// <remarks>
    /// When every attempt fails and the last outcome is an exception, that exception is rethrown with its original stack
    /// trace. An exception that the policy of <paramref name="hedging"/> refuses is not a failure: it is rethrown at
    /// once. An exception from a callback stops the call.
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
        bool continueOnCapturedContext)
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
            continueOnCapturedContext);
        Func<HedgingEventArgs, ValueTask>? onHedging = hedging.OnHedging;
        TimeSpan delay = hedging.Delay;

        // A zero delay starts all the attempts at once, and an infinite delay starts an attempt only after a failure.
        bool useDelay = delay > TimeSpan.Zero;
        bool restartDelay = false;
        ITimer? delayTimer = null;
        TaskCompletionSource? delaySignal = null;

        // This wakes the engine when the outer token is cancelled, so that the call does not wait for an attempt that
        // ignores its token.
        TaskCompletionSource? cancelSignal = null;
        CancellationTokenRegistration cancelRegistration = default;

        Exception? lastException = null;
        T lastResult = default!;
        bool holdsResult = false;

        try
        {
            call.StartNext();
            restartDelay = useDelay;
            if (delay == TimeSpan.Zero)
            {
                while (call.CanStartNext)
                {
                    await BeforeHedgedAttemptAsync(null).ConfigureAwait(continueOnCapturedContext);
                    call.StartNext();
                }
            }

            while (true)
            {
                // Read the outcomes in the sequence in which the attempts started.
                HedgedAttempt<T>? completed = call.TakeCompleted();
                if (completed is null)
                {
                    if (delaySignal is { Task.IsCompleted: true })
                    {
                        // The delay expired before an outcome won.
                        StopDelay();
                        await BeforeHedgedAttemptAsync(null).ConfigureAwait(continueOnCapturedContext);
                        call.StartNext();
                        restartDelay = true;
                        continue;
                    }

                    if (outer.IsCancellationRequested)
                    {
                        // Only the caller's token and the total timeout can cancel the outer token.
                        caller.ThrowIfCancellationRequested();
                        throw new RetryTimeoutException(total);
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

                            call.StartNext();
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
                        if (!hedging.Policy.CanRetry(failure))
                        {
                            // An exception that the policy refuses is not a failure, so it wins.
                            ExceptionDispatchInfo.Throw(failure);
                        }

                        if (call.CanStartNext)
                        {
                            StopDelay();
                            await BeforeHedgedAttemptAsync(failure).ConfigureAwait(continueOnCapturedContext);
                            call.StartNext();
                            restartDelay = true;
                        }
                        else
                        {
                            ReleaseLastResult();
                            lastException = failure;
                        }

                        break;

                    default:
                        // A cancellation by the caller, the total timeout, or an exception from a callback stops the call.
                        // Rethrow the exception with its original stack trace.
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

                    ExceptionDispatchInfo.Throw(lastException!);
                }
            }
        }
        finally
        {
            StopDelay();
            cancelRegistration.Dispose();

            // Cancel the attempts that have not won, and do not wait for them.
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
            ThrowIfStopped(caller, totalCts, total);
            if (onHedging is not null)
            {
                await onHedging(new HedgingEventArgs(call.NextAttemptNumber, exception)).ConfigureAwait(continueOnCapturedContext);
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

    private static void ThrowIfStopped(CancellationToken caller, CancellationTokenSource? totalCts, TimeSpan total)
    {
        caller.ThrowIfCancellationRequested();
        if (totalCts is { IsCancellationRequested: true })
        {
            throw new RetryTimeoutException(total);
        }
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
            bool continueOnCapturedContext)
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

        public bool CanStartNext => this.started < this.attempts.Length;

        public int NextAttemptNumber => this.started + 1;

        public bool HasPendingAttempts => this.pending.Count > 0;

        public void StartNext()
        {
            // Do not start an attempt that cannot run.
            ThrowIfStopped(this.Caller, this.TotalCts, this.Total);

            int number = this.started + 1;
            Func<CancellationToken, ValueTask<T>> operation = this.generator?.Invoke(number) ?? this.primary;

            // Record the attempt before it starts, so that the engine releases it whatever happens next.
            HedgedAttempt<T> attempt = new(this);
            this.attempts[this.started++] = attempt;
            this.pending.Add(attempt);
            attempt.Start(operation);
        }

        public HedgedAttempt<T>? TakeCompleted()
        {
            for (int i = 0; i < this.pending.Count; i++)
            {
                HedgedAttempt<T> attempt = this.pending[i];
                if (attempt.Task.IsCompleted)
                {
                    this.pending.RemoveAt(i);
                    return attempt;
                }
            }

            return null;
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

        private readonly HedgedCall<T> call;
        private readonly CancellationTokenSource cts;
        private readonly CancellationTokenRegistration registration;
        private Task? task;
        private bool hasResult;

        // Running until the attempt completes or the engine abandons it, whichever happens first.
        private int state;

        // One reference for the engine and one for the attempt. The last to release disposes the source.
        private int references = 2;

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

        public void Start(Func<CancellationToken, ValueTask<T>> operation)
        {
            this.task = this.RunAsync(operation);
        }

        /// <summary>
        /// Stops the engine from reading the outcome of the attempt, and cancels the attempt if it is still running.
        /// </summary>
        public void Abandon()
        {
            if (Interlocked.CompareExchange(ref this.state, Abandoned, Running) == Running)
            {
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

        public void Release()
        {
            if (Interlocked.Decrement(ref this.references) == 0)
            {
                // Dispose the registration first, so that the outer token cannot cancel a disposed source.
                this.registration.Dispose();
                this.cts.Dispose();
            }
        }

        private async Task RunAsync(Func<CancellationToken, ValueTask<T>> operation)
        {
            HedgedCall<T> call = this.call;
            CircuitBreaker? breaker = call.CircuitBreaker;
            bool acquired = false;
            bool probe = false;
            bool reported = false;
            try
            {
                // The circuit breaker is outside the attempt timeout.
                if (breaker is not null)
                {
                    acquired = breaker.TryAcquire(out probe, out BrokenCircuitException? rejection, out CircuitStateChange? change);
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
                    if (!acquired)
                    {
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
                    if (!acquired)
                    {
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
                this.Outcome = AttemptOutcome.Stopped;
                this.Exception = exception;
            }
            finally
            {
                if (acquired && !reported)
                {
                    // The attempt has no outcome for the breaker. If it was the test call, let another call make it.
                    breaker!.Abandon(probe);
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

        private void DisposeResult()
        {
            if (this.hasResult)
            {
                DisposeQuietly(this.Result);
            }
        }
    }
}
