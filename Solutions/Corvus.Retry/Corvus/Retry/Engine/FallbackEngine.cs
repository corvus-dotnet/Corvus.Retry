// <copyright file="FallbackEngine.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Corvus.Retry.Policies;

/// <summary>
/// Applies a <see cref="Fallback{T}"/> to the outcome of a call to <see cref="RetryEngine"/> or <see cref="HedgingEngine"/>.
/// </summary>
/// <remarks>
/// The fallback is the outermost part of a call. It handles only an exception that the engine marks as the outcome of
/// the attempts in a <see cref="CallOutcome"/>, so only a call with a fallback needs the marker.
/// </remarks>
internal static class FallbackEngine
{
    /// <summary>
    /// Awaits the outcome of the attempts, and applies the fallback to it.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="core">The call to the engine.</param>
    /// <param name="fallback">The fallback.</param>
    /// <param name="outcome">The marker that the engine sets for the exception that is the outcome of the attempts.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context after each await.</param>
    /// <returns>The result of the attempts, or the value of the fallback.</returns>
    internal static async ValueTask<T> WithFallbackAsync<T>(ValueTask<T> core, Fallback<T> fallback, CallOutcome outcome, CancellationToken cancellationToken, bool continueOnCapturedContext)
    {
        T result;
        try
        {
            result = await core.ConfigureAwait(continueOnCapturedContext);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled, so the fallback does not apply. The engine never makes a cancellation by the caller
            // the outcome, but the outcome can be a cancellation that the operation caused itself, when the caller
            // cancels after the attempt, for example while the engine awaits the state change callback of the breaker.
            throw;
        }
        catch (Exception ex) when (ReferenceEquals(ex, outcome.Exception))
        {
            // The filter lets an exception from a callback, the strategy or a policy of the factory propagate. It is not
            // an outcome of the attempts, so the fallback does not apply.
            // The policy is evaluated here, not in a filter, so that an exception from the policy propagates. It is asked
            // about the exceptions that the operation threw, not the AggregateException that wraps them.
            bool handles = ex is AggregateException aggregate
                ? fallback.Policy.CanRetryAll(aggregate)
                : fallback.Policy.CanRetry(ex);
            if (!handles)
            {
                throw;
            }

            return await FallBackAsync(fallback, new FallbackEventArgs<T>(ex, default, cancellationToken), continueOnCapturedContext).ConfigureAwait(continueOnCapturedContext);
        }

        if (fallback.AppliesToResult is not { } appliesToResult || !AppliesTo(appliesToResult, result))
        {
            return result;
        }

        // OnFallback sees the rejected result before the factory releases it, as OnRetry sees a rejected result before a
        // retry. The action runs after the release, so the caller never gets a result that is still open.
        FallbackEventArgs<T> args = new(null, result, cancellationToken);
        if (fallback.OnFallback is { } onFallback)
        {
            try
            {
                await onFallback(args).ConfigureAwait(continueOnCapturedContext);
            }
            catch
            {
                DisposeQuietly(result);
                throw;
            }
        }

        // The caller does not get the result, so release it. An exception from the disposal stops the call.
        (result as IDisposable)?.Dispose();
        return await fallback.Action(args).ConfigureAwait(continueOnCapturedContext);
    }

    // Asks the predicate about a result. An exception from the predicate stops the call. The caller does not get the
    // result, so the factory disposes it, and ignores an exception from the disposal.
    private static bool AppliesTo<T>(Func<T, bool> appliesToResult, T result)
    {
        try
        {
            return appliesToResult(result);
        }
        catch
        {
            DisposeQuietly(result);
            throw;
        }
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

    private static async ValueTask<T> FallBackAsync<T>(Fallback<T> fallback, FallbackEventArgs<T> args, bool continueOnCapturedContext)
    {
        if (fallback.OnFallback is { } onFallback)
        {
            await onFallback(args).ConfigureAwait(continueOnCapturedContext);
        }

        return await fallback.Action(args).ConfigureAwait(continueOnCapturedContext);
    }
}
