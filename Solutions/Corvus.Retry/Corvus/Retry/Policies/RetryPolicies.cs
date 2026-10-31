// <copyright file="RetryPolicies.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Net.Http;

/// <summary>
/// Creates <see cref="IRetryPolicy"/> instances, and provides shared ones.
/// </summary>
/// <remarks>
/// <para>
/// Every policy that this class creates is immutable. Many threads can use one instance at the same time. See also
/// the extension members <c>Or</c>, <c>And</c> and <c>Not</c> in <see cref="RetryPolicyExtensions"/>.
/// </para>
/// <para>
/// A policy decides only about exceptions. No policy makes the library retry an
/// <see cref="OperationCanceledException"/> that the caller's cancellation token caused: the library does not ask
/// the policy about it.
/// </para>
/// </remarks>
public static class RetryPolicies
{
    /// <summary>
    /// Gets a shared policy that retries transient HTTP failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This accepts an <see cref="HttpRequestException"/> with no status code, or with status 408 (Request Timeout),
    /// 429 (Too Many Requests), or 500 or more. It also accepts a <see cref="RetryTimeoutException"/>.
    /// </para>
    /// <para>
    /// It refuses all other exceptions. To retry results as well as exceptions, use
    /// <see cref="IsTransientHttpResponse(HttpResponseMessage)"/>.
    /// </para>
    /// </remarks>
    public static IRetryPolicy TransientHttp { get; } = new TransientHttpRetryPolicy();

    /// <summary>
    /// Creates a policy that asks a predicate whether to retry.
    /// </summary>
    /// <param name="predicate">
    /// The predicate that returns true when the operation can be retried. Many calls can use it at the same time.
    /// </param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public static IRetryPolicy FromPredicate(Func<Exception, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return new PredicateRetryPolicy(predicate);
    }

    /// <summary>
    /// Creates a policy that retries exceptions of type <typeparamref name="TException"/>.
    /// </summary>
    /// <typeparam name="TException">The type of exception to retry. Exceptions of derived types also match.</typeparam>
    /// <param name="predicate">
    /// An optional predicate that returns true when an exception of that type can be retried. Without it, every
    /// exception of that type can be retried.
    /// </param>
    /// <returns>
    /// The policy. Use <see cref="ExceptionTypeRetryPolicy.Or{TException}(Func{TException, bool}?)"/> to add more types.
    /// </returns>
    public static ExceptionTypeRetryPolicy Handle<TException>(Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        return ExceptionTypeRetryPolicy.Create(predicate);
    }

    /// <summary>
    /// Creates a policy that retries when the exception, or an exception inside it, is of type
    /// <typeparamref name="TException"/>.
    /// </summary>
    /// <typeparam name="TException">The type of exception to retry. Exceptions of derived types also match.</typeparam>
    /// <param name="predicate">
    /// An optional predicate that returns true when an exception of that type can be retried. Without it, every
    /// exception of that type can be retried.
    /// </param>
    /// <returns>The policy.</returns>
    /// <remarks>
    /// The policy examines the exception, its <see cref="Exception.InnerException"/> chain, and the
    /// <see cref="AggregateException.InnerExceptions"/> of an <see cref="AggregateException"/>, depth first.
    /// </remarks>
    public static IRetryPolicy HandleInner<TException>(Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        return new InnerExceptionRetryPolicy(ExceptionTypeRetryPolicy.CreateMatch(predicate));
    }

    /// <summary>
    /// Creates a policy that retries an exception that another policy refuses.
    /// </summary>
    /// <param name="policy">The policy to negate.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public static IRetryPolicy Not(IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new NotRetryPolicy(policy);
    }

    /// <summary>
    /// Creates a policy that retries an exception that one or more of the policies accept.
    /// </summary>
    /// <param name="policies">The policies. With no policies, the policy never retries.</param>
    /// <returns>The policy. It asks the policies in order, and stops at the first that accepts.</returns>
    /// <exception cref="ArgumentException"><paramref name="policies"/> contains <see langword="null"/>.</exception>
    public static IRetryPolicy Any(params ReadOnlySpan<IRetryPolicy> policies)
    {
        return new AnyRetryPolicy(ToArray(policies));
    }

    /// <summary>
    /// Creates a policy that retries an exception that all of the policies accept.
    /// </summary>
    /// <param name="policies">The policies. With no policies, nothing objects to a retry, so the policy retries.</param>
    /// <returns>
    /// The policy. It asks the policies in order, and stops at the first that refuses. It gives the same result as
    /// <see cref="AggregatePolicy"/>, but cannot change.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="policies"/> contains <see langword="null"/>.</exception>
    public static IRetryPolicy All(params ReadOnlySpan<IRetryPolicy> policies)
    {
        return new AllRetryPolicy(ToArray(policies));
    }

    /// <summary>
    /// Determines whether an HTTP response shows a transient failure.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>True for status 408 (Request Timeout), 429 (Too Many Requests), and 500 or more; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <remarks>Use this to retry a result that shows a transient failure.</remarks>
    public static bool IsTransientHttpResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return TransientHttpRetryPolicy.IsTransient(response.StatusCode);
    }

    private static IRetryPolicy[] ToArray(ReadOnlySpan<IRetryPolicy> policies)
    {
        // A copy, so that a change to the caller's array cannot change the policy.
        IRetryPolicy[] copy = policies.ToArray();
        foreach (IRetryPolicy policy in copy)
        {
            if (policy is null)
            {
                throw new ArgumentException("The policies must not contain null.", nameof(policies));
            }
        }

        return copy;
    }
}
