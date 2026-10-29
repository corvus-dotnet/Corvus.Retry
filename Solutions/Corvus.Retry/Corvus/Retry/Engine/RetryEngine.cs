// <copyright file="RetryEngine.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// The retry loops that every retry API runs.
/// </summary>
/// <remarks>
/// <para>
/// A call has its own state: the attempt number, the previous delay, and the cancellation sources for its timeouts.
/// The strategy and the policy hold no state for a call, so many calls can share them.
/// </para>
/// <para>
/// When there is no timeout and no callback, and every attempt completes synchronously, a call allocates nothing.
/// </para>
/// </remarks>
internal static class RetryEngine
{
    private static readonly Action<object?> CancelSource = static s => ((CancellationTokenSource)s!).Cancel();

    /// <summary>
    /// Runs an asynchronous operation, and retries it when it fails.
    /// </summary>
    /// <typeparam name="TOp">The type of the adapter for the operation.</typeparam>
    /// <typeparam name="T">The type of the result of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="caller">The caller's token. Its cancellation stops the call with an <see cref="OperationCanceledException"/>.</param>
    /// <param name="strategy">The strategy that decides whether to retry, and the delay before the retry.</param>
    /// <param name="policy">The policy that decides whether an exception can be retried.</param>
    /// <param name="timeProvider">The time provider for the delays and the timeouts.</param>
    /// <param name="attemptTimeout">
    /// The time limit for each attempt, or <see langword="null"/> for no limit. It must be greater than zero and not
    /// greater than <see cref="RetryDelay.MaxSupported"/>.
    /// </param>
    /// <param name="totalTimeout">
    /// The time limit for all the attempts and all the delays, or <see langword="null"/> for no limit. It must be greater
    /// than zero and not greater than <see cref="RetryDelay.MaxSupported"/>.
    /// </param>
    /// <param name="onRetry">A callback that the engine awaits before each delay, or <see langword="null"/>.</param>
    /// <param name="circuitBreaker">
    /// The circuit breaker that permits each attempt and counts its outcome, or <see langword="null"/>. It is inside the
    /// retry loop and outside the attempt timeout.
    /// </param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context after each await.</param>
    /// <param name="outcome">
    /// Receives the exception that the call throws as the outcome of its attempts, or <see langword="null"/>. The engine
    /// does not set it for an exception from a callback, the strategy, a policy, or the circuit breaker.
    /// </param>
    /// <returns>The result of the first attempt that succeeds, or the last result that the operation marks as a failure.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <exception cref="RetryTimeoutException">
    /// The total timeout expired, or the last attempt timed out and was not retried.
    /// </exception>
    /// <exception cref="BrokenCircuitException">
    /// The circuit breaker rejected the last attempt, and the strategy or the policy refused a retry.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When the strategy or the policy refuses a retry, the exception of the last attempt is rethrown with its
    /// original stack trace. An exception from <paramref name="onRetry"/>, or from the state change callback of the
    /// circuit breaker, stops the call.
    /// </para>
    /// <para>
    /// An attempt that the circuit breaker rejects is a failure for the strategy and the policy, but it is not a call for
    /// the breaker. A cancellation by the caller is not reported to the breaker as a call.
    /// </para>
    /// <para>
    /// The engine disposes each <see cref="IDisposable"/> result that it does not return. If the call stops with an
    /// exception after a result, the engine disposes that result, and ignores an exception from its disposal. When the
    /// total timeout expires between attempts, the inner exception of the <see cref="RetryTimeoutException"/> is the
    /// exception of the last attempt, or <see langword="null"/> if the last attempt returned a result.
    /// </para>
    /// </remarks>
    internal static async ValueTask<T> RunAsync<TOp, T>(
        TOp operation,
        CancellationToken caller,
        IRetryStrategy strategy,
        IRetryPolicy policy,
        TimeProvider timeProvider,
        TimeSpan? attemptTimeout,
        TimeSpan? totalTimeout,
        Func<RetryEventArgs, ValueTask>? onRetry,
        CircuitBreaker? circuitBreaker,
        bool continueOnCapturedContext,
        CallOutcome? outcome = null)
        where TOp : struct, IRetryOperation<T>
    {
        // The total timeout cancels the token that every attempt and every delay uses. The caller's token cancels it too.
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

        try
        {
            TimeSpan previousDelay = TimeSpan.Zero;
            Exception? lastFailure = null;
            for (int attempt = 1; ; attempt++)
            {
                caller.ThrowIfCancellationRequested();
                ThrowIfTotalTimeoutExpired(totalCts, total, lastFailure, outcome);

                T result = default!;
                Exception? failure = null;
                TimeSpan delay = TimeSpan.Zero;

                // The circuit breaker is outside the attempt timeout, so a rejected attempt starts no timer.
                bool acquired = false;
                int probe = 0;
                if (circuitBreaker is not null)
                {
                    acquired = circuitBreaker.TryAcquire(out probe, out BrokenCircuitException? rejection, out CircuitStateChange? change);
                    if (change.HasValue)
                    {
                        try
                        {
                            await circuitBreaker.NotifyAsync(change).ConfigureAwait(continueOnCapturedContext);
                        }
                        catch
                        {
                            // The attempt does not run, so let another call make the test call.
                            circuitBreaker.Abandon(probe);
                            throw;
                        }
                    }

                    failure = rejection;
                }

                if (failure is not null)
                {
                    // A rejected attempt is a failure for the strategy and the policy, but it is not a call for the breaker.
                    if (strategy.GetRetryDelay(new RetryAttempt(attempt, failure, null, previousDelay)) is not TimeSpan rejectedDelay
                        || !policy.CanRetry(failure))
                    {
                        throw SetOutcome(outcome, failure);
                    }

                    delay = RetryDelay.Clamp(rejectedDelay);
                }
                else
                {
                    // The attempt timeout is linked to the outer token, so that the total timeout also cancels the attempt.
                    CancellationTokenSource? attemptCts = null;
                    CancellationTokenRegistration attemptRegistration = default;
                    CancellationToken token = outer;
                    if (attemptTimeout is TimeSpan timeout)
                    {
                        attemptCts = new CancellationTokenSource(timeout, timeProvider);
                        attemptRegistration = outer.UnsafeRegister(CancelSource, attemptCts);
                        token = attemptCts.Token;
                    }

                    try
                    {
                        result = await operation.InvokeAsync(token).ConfigureAwait(continueOnCapturedContext);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        if (exception is OperationCanceledException cancellation)
                        {
                            switch (Classify(caller, totalCts, attemptCts))
                            {
                                case CancellationCause.Caller:
                                    // A cancellation by the caller is not a call and not a failure.
                                    if (acquired)
                                    {
                                        circuitBreaker!.Abandon(probe);
                                    }

                                    throw;

                                case CancellationCause.TotalTimeout:
                                    failure = new RetryTimeoutException(total, cancellation);
                                    if (acquired)
                                    {
                                        await circuitBreaker!.NotifyAsync(ReportFailure(circuitBreaker, failure, probe)).ConfigureAwait(continueOnCapturedContext);
                                    }

                                    throw SetOutcome(outcome, failure);

                                case CancellationCause.AttemptTimeout:
                                    failure = new RetryTimeoutException(attemptTimeout.GetValueOrDefault(), cancellation);
                                    break;

                                default:
                                    // The operation cancelled itself. The strategy and the policy decide, as for any exception.
                                    break;
                            }
                        }

                        if (acquired)
                        {
                            await circuitBreaker!.NotifyAsync(ReportFailure(circuitBreaker, failure, probe)).ConfigureAwait(continueOnCapturedContext);
                        }

                        // The policy is not consulted when the strategy has no retries left.
                        if (strategy.GetRetryDelay(new RetryAttempt(attempt, failure, null, previousDelay)) is not TimeSpan retryDelay
                            || !policy.CanRetry(failure))
                        {
                            SetOutcome(outcome, failure);
                            if (ReferenceEquals(failure, exception))
                            {
                                // Rethrow from inside the catch block so that the exception keeps its original stack trace.
                                throw;
                            }

                            throw failure;
                        }

                        delay = RetryDelay.Clamp(retryDelay);
                    }
                    finally
                    {
                        // Dispose the registration first, so that the outer token cannot cancel a disposed source.
                        attemptRegistration.Dispose();
                        attemptCts?.Dispose();
                    }

                    if (failure is null)
                    {
                        try
                        {
                            bool resultIsFailure;
                            if (acquired)
                            {
                                try
                                {
                                    resultIsFailure = operation.IsFailure(result);
                                }
                                catch
                                {
                                    // The result has no outcome for the breaker, so do not count it.
                                    circuitBreaker!.Abandon(probe);
                                    throw;
                                }

                                CircuitStateChange? change = resultIsFailure ? circuitBreaker!.OnFailure(null, probe) : circuitBreaker!.OnSuccess(probe);
                                await circuitBreaker.NotifyAsync(change).ConfigureAwait(continueOnCapturedContext);
                            }
                            else
                            {
                                resultIsFailure = operation.IsFailure(result);
                            }

                            // When the strategy has no retries left for a result that is a failure, the caller gets that result.
                            if (!resultIsFailure
                                || strategy.GetRetryDelay(new RetryAttempt(attempt, null, result, previousDelay)) is not TimeSpan retryDelay)
                            {
                                return result;
                            }

                            delay = RetryDelay.Clamp(retryDelay);
                        }
                        catch
                        {
                            // The caller does not get the result, so release it.
                            DisposeQuietly(result);
                            throw;
                        }
                    }
                }

                try
                {
                    // Do not report a retry that will not run.
                    caller.ThrowIfCancellationRequested();
                    ThrowIfTotalTimeoutExpired(totalCts, total, failure, outcome);

                    if (onRetry is not null)
                    {
                        await onRetry(new RetryEventArgs(failure, failure is null ? (object?)result : null, delay, attempt)).ConfigureAwait(continueOnCapturedContext);
                    }
                }
                catch when (failure is null)
                {
                    // The call stops with a result that the caller does not get, so release it.
                    DisposeQuietly(result);
                    throw;
                }

                if (failure is null && result is IDisposable disposable)
                {
                    // The result is not returned to the caller, so release it after the callback has seen it.
                    disposable.Dispose();
                }

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await RetryDelay.WaitAsync(delay, timeProvider, outer).ConfigureAwait(continueOnCapturedContext);
                    }
                    catch (OperationCanceledException) when (!caller.IsCancellationRequested && totalCts is not null)
                    {
                        // Only the caller's token and the total timeout can cancel the outer token. The delay is between
                        // two attempts, so the inner exception is the failure of the last attempt, as at the check
                        // before an attempt. It is null when the last attempt returned a result that is a failure.
                        throw SetOutcome(outcome, new RetryTimeoutException(total, failure));
                    }
                }

                previousDelay = delay;
                lastFailure = failure;
            }
        }
        finally
        {
            totalRegistration.Dispose();
            totalCts?.Dispose();
        }
    }

    /// <summary>
    /// Runs a synchronous operation, and retries it when it fails.
    /// </summary>
    /// <typeparam name="TOp">The type of the adapter for the operation.</typeparam>
    /// <typeparam name="T">The type of the result of the operation.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="caller">The caller's token. Its cancellation stops the call with an <see cref="OperationCanceledException"/>.</param>
    /// <param name="strategy">The strategy that decides whether to retry, and the delay before the retry.</param>
    /// <param name="policy">The policy that decides whether an exception can be retried.</param>
    /// <param name="timeProvider">The time provider for the delays.</param>
    /// <param name="onRetry">
    /// A callback that the engine calls before each delay, or <see langword="null"/>. If the callback returns a task
    /// that is not complete, the thread waits for it. The callback runs with no <see cref="SynchronizationContext"/>.
    /// </param>
    /// <returns>The result of the first attempt that succeeds.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <remarks>
    /// This loop blocks the thread for each delay. It never waits on an asynchronous delay.
    /// </remarks>
    internal static T Run<TOp, T>(
        TOp operation,
        CancellationToken caller,
        IRetryStrategy strategy,
        IRetryPolicy policy,
        TimeProvider timeProvider,
        Func<RetryEventArgs, ValueTask>? onRetry)
        where TOp : struct, IRetrySyncOperation<T>
    {
        TimeSpan previousDelay = TimeSpan.Zero;
        for (int attempt = 1; ; attempt++)
        {
            caller.ThrowIfCancellationRequested();

            Exception failure;
            TimeSpan delay;
            try
            {
                return operation.Invoke();
            }
            catch (Exception exception)
            {
                // A cancellation that the caller caused is never retried. The policy is not consulted when the
                // strategy has no retries left.
                if ((exception is OperationCanceledException && caller.IsCancellationRequested)
                    || strategy.GetRetryDelay(new RetryAttempt(attempt, exception, null, previousDelay)) is not TimeSpan retryDelay
                    || !policy.CanRetry(exception))
                {
                    // Rethrow from inside the catch block so that the exception keeps its original stack trace.
                    throw;
                }

                failure = exception;
                delay = RetryDelay.Clamp(retryDelay);
            }

            // Do not report a retry that will not run.
            caller.ThrowIfCancellationRequested();

            if (onRetry is not null)
            {
                SynchronousCallback.InvokeAndWait(onRetry, new RetryEventArgs(failure, null, delay, attempt));
            }

            RetryDelay.Wait(delay, timeProvider, caller);
            previousDelay = delay;
        }
    }

    /// <summary>
    /// Finds the source that cancelled an attempt.
    /// </summary>
    /// <param name="caller">The caller's token.</param>
    /// <param name="total">The source for the total timeout, or <see langword="null"/> if there is none.</param>
    /// <param name="attempt">The source for the attempt timeout, or <see langword="null"/> if there is none.</param>
    /// <returns>
    /// The first cancelled source, in this sequence: the caller, the total timeout, the attempt timeout. When two
    /// sources are cancelled at the same instant, the first in the sequence wins.
    /// </returns>
    internal static CancellationCause Classify(CancellationToken caller, CancellationTokenSource? total, CancellationTokenSource? attempt)
    {
        if (caller.IsCancellationRequested)
        {
            return CancellationCause.Caller;
        }

        if (total is { IsCancellationRequested: true })
        {
            return CancellationCause.TotalTimeout;
        }

        if (attempt is { IsCancellationRequested: true })
        {
            return CancellationCause.AttemptTimeout;
        }

        return CancellationCause.None;
    }

    // Reports a failed attempt to the breaker. The policy of the breaker is the caller's code. If it throws, the attempt
    // has no outcome for the breaker, so let another call make the test call.
    private static CircuitStateChange? ReportFailure(CircuitBreaker circuitBreaker, Exception failure, int probe)
    {
        try
        {
            return circuitBreaker.OnFailure(failure, probe);
        }
        catch
        {
            circuitBreaker.Abandon(probe);
            throw;
        }
    }

    // The last failure is the inner exception, so that the caller can see why the attempts did not succeed in time.
    private static void ThrowIfTotalTimeoutExpired(CancellationTokenSource? totalCts, TimeSpan total, Exception? lastFailure, CallOutcome? outcome)
    {
        if (totalCts is { IsCancellationRequested: true })
        {
            throw SetOutcome(outcome, new RetryTimeoutException(total, lastFailure));
        }
    }

    // Records an exception that the call throws as the outcome of its attempts, so that the fallback can tell it from an
    // exception that a callback, the strategy or a policy throws.
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
        Justification = "The call already stops with another exception. That exception is more important to the caller.")]
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
                // The call already has an outcome, so the exception has no receiver.
            }
        }
    }
}
