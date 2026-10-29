// <copyright file="ExceptionTypeRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries exceptions of one or more types, each with an optional predicate.
/// </summary>
/// <remarks>
/// <para>
/// Create one with <see cref="RetryPolicies.Handle{TException}(Func{TException, bool}?)"/>, and add more types with
/// <see cref="Or{TException}(Func{TException, bool}?)"/>. An exception matches a type when it is an instance of that
/// type or of a type that derives from it.
/// </para>
/// <para>
/// This type is immutable: <see cref="Or{TException}(Func{TException, bool}?)"/> returns a new policy, and does not
/// change this one. Many threads can use one instance at the same time.
/// </para>
/// </remarks>
public sealed class ExceptionTypeRetryPolicy : IRetryPolicy
{
    private readonly Func<Exception, bool>[] handlers;

    private ExceptionTypeRetryPolicy(Func<Exception, bool>[] handlers)
    {
        this.handlers = handlers;
    }

    /// <summary>
    /// Creates a policy that retries exceptions of this policy's types, and also exceptions of type
    /// <typeparamref name="TException"/>.
    /// </summary>
    /// <typeparam name="TException">The type of exception to retry.</typeparam>
    /// <param name="predicate">
    /// An optional predicate that returns true when an exception of type <typeparamref name="TException"/> can be
    /// retried. Without it, every exception of that type can be retried.
    /// </param>
    /// <returns>A new policy. This policy does not change.</returns>
    public ExceptionTypeRetryPolicy Or<TException>(Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        Func<Exception, bool>[] combined = new Func<Exception, bool>[this.handlers.Length + 1];
        this.handlers.CopyTo(combined, 0);
        combined[^1] = CreateMatch(predicate);
        return new ExceptionTypeRetryPolicy(combined);
    }

    /// <inheritdoc/>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (Func<Exception, bool> handler in this.handlers)
        {
            if (handler(exception))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a policy that retries exceptions of type <typeparamref name="TException"/>.
    /// </summary>
    /// <typeparam name="TException">The type of exception to retry.</typeparam>
    /// <param name="predicate">An optional predicate for exceptions of that type.</param>
    /// <returns>The policy.</returns>
    internal static ExceptionTypeRetryPolicy Create<TException>(Func<TException, bool>? predicate)
        where TException : Exception
    {
        return new ExceptionTypeRetryPolicy([CreateMatch(predicate)]);
    }

    /// <summary>
    /// Creates a function that returns true for an exception of type <typeparamref name="TException"/> that the
    /// optional predicate accepts.
    /// </summary>
    /// <typeparam name="TException">The type of exception to match.</typeparam>
    /// <param name="predicate">An optional predicate for exceptions of that type.</param>
    /// <returns>The function.</returns>
    /// <remarks>This allocates only when the policy is created, not when it decides.</remarks>
    internal static Func<Exception, bool> CreateMatch<TException>(Func<TException, bool>? predicate)
        where TException : Exception
    {
        if (predicate is null)
        {
            return static exception => exception is TException;
        }

        return exception => exception is TException typed && predicate(typed);
    }
}
