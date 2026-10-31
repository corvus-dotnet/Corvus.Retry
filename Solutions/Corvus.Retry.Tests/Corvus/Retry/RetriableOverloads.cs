// <copyright file="RetriableOverloads.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Finds and calls each public <see cref="Retriable"/> overload by reflection, so that a test can use the overload as
/// a <c>[DynamicData]</c> dimension and pass each of its parameters a value whose effect the test can see.
/// </summary>
/// <remarks>
/// The generic overloads are made for <see cref="string"/>. The obsolete overloads only stop an asynchronous operation
/// from binding to <c>Retry</c>, and no call can reach them, so they are not included.
/// </remarks>
internal static class RetriableOverloads
{
    /// <summary>
    /// Gets every public overload that a call can reach.
    /// </summary>
    public static IEnumerable<MethodInfo> All
    {
        get
        {
            return typeof(Retriable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name is nameof(Retriable.Retry) or nameof(Retriable.RetryAsync))
                .Where(m => m.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(string)) : m);
        }
    }

    /// <summary>
    /// Gets a description of the overload that is unique among the overloads, such as
    /// <c>RetryAsync(Func&lt;CancellationToken, Task&gt;, CancellationToken)</c>.
    /// </summary>
    /// <param name="method">The overload.</param>
    /// <returns>The description.</returns>
    public static string Describe(MethodInfo method)
    {
        return $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => FriendlyName(p.ParameterType)))})";
    }

    /// <summary>
    /// Finds the overload that <see cref="Describe"/> describes.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <returns>The overload.</returns>
    public static MethodInfo Find(string description)
    {
        return All.Single(m => Describe(m) == description);
    }

    /// <summary>
    /// Gets a value that tells whether the overload has a parameter of the given type.
    /// </summary>
    /// <typeparam name="T">The type of the parameter.</typeparam>
    /// <param name="method">The overload.</param>
    /// <returns><see langword="true"/> if the overload has the parameter.</returns>
    public static bool Has<T>(MethodInfo method)
    {
        return method.GetParameters().Any(p => p.ParameterType == typeof(T));
    }

    /// <summary>
    /// Gets a value that tells whether the overload runs the synchronous loop.
    /// </summary>
    /// <param name="method">The overload.</param>
    /// <returns><see langword="true"/> for <c>Retry</c>; <see langword="false"/> for <c>RetryAsync</c>.</returns>
    public static bool IsSynchronous(MethodInfo method) => method.Name == nameof(Retriable.Retry);

    /// <summary>
    /// Gets a value that tells whether the operation of the overload takes a cancellation token.
    /// </summary>
    /// <param name="method">The overload.</param>
    /// <returns><see langword="true"/> if the first parameter is a function of a <see cref="CancellationToken"/>.</returns>
    public static bool OperationTakesToken(MethodInfo method)
    {
        Type operation = method.GetParameters()[0].ParameterType;
        return operation.IsGenericType && operation.GetGenericArguments()[0] == typeof(CancellationToken);
    }

    /// <summary>
    /// Calls the overload, and passes each argument that it has a parameter for.
    /// </summary>
    /// <param name="method">The overload.</param>
    /// <param name="operation">
    /// The operation. It gets the token that the overload passes to the operation, or <see cref="CancellationToken.None"/>
    /// for an operation that takes no token. For a synchronous overload, the task that it returns must be complete.
    /// </param>
    /// <param name="cancellationToken">The token, for an overload that takes one.</param>
    /// <param name="strategy">The strategy, for an overload that takes one.</param>
    /// <param name="policy">The policy, for an overload that takes one.</param>
    /// <param name="continueOnCapturedContext">The value, for an overload that takes one.</param>
    /// <param name="onRetry">The callback, for an overload that takes one.</param>
    /// <returns>
    /// The result of the call for an overload that returns one, otherwise <see langword="null"/>. The exception of the
    /// call is thrown as it is, without a <see cref="TargetInvocationException"/>.
    /// </returns>
    public static async Task<string?> InvokeAsync(
        MethodInfo method,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken = default,
        IRetryStrategy? strategy = null,
        IRetryPolicy? policy = null,
        bool continueOnCapturedContext = false,
        Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        object? returned = Invoke(method, operation, cancellationToken, strategy, policy, continueOnCapturedContext, onRetry);
        switch (returned)
        {
            case Task<string> resultTask:
                return await resultTask.ConfigureAwait(false);

            case Task task:
                await task.ConfigureAwait(false);
                return null;

            default:
                return (string?)returned;
        }
    }

    /// <summary>
    /// Calls the overload, and returns what it returns without awaiting it.
    /// </summary>
    /// <param name="method">The overload.</param>
    /// <param name="operation">The operation. See <see cref="InvokeAsync"/>.</param>
    /// <param name="cancellationToken">The token, for an overload that takes one.</param>
    /// <param name="strategy">The strategy, for an overload that takes one.</param>
    /// <param name="policy">The policy, for an overload that takes one.</param>
    /// <param name="continueOnCapturedContext">The value, for an overload that takes one.</param>
    /// <param name="onRetry">The callback, for an overload that takes one.</param>
    /// <returns>The task, the result, or <see langword="null"/>.</returns>
    public static object? Invoke(
        MethodInfo method,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken = default,
        IRetryStrategy? strategy = null,
        IRetryPolicy? policy = null,
        bool continueOnCapturedContext = false,
        Func<RetryEventArgs, ValueTask>? onRetry = null)
    {
        object?[] arguments = [.. method.GetParameters().Select(p => Argument(p.ParameterType, operation, cancellationToken, strategy, policy, continueOnCapturedContext, onRetry))];
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
    }

    private static object? Argument(
        Type type,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken,
        IRetryStrategy? strategy,
        IRetryPolicy? policy,
        bool continueOnCapturedContext,
        Func<RetryEventArgs, ValueTask>? onRetry)
    {
        if (type == typeof(CancellationToken))
        {
            return cancellationToken;
        }

        if (type == typeof(IRetryStrategy))
        {
            return strategy ?? RetryStrategies.Default;
        }

        if (type == typeof(IRetryPolicy))
        {
            return policy ?? AnyExceptionPolicy.Default;
        }

        if (type == typeof(bool))
        {
            return continueOnCapturedContext;
        }

        if (type == typeof(Func<RetryEventArgs, ValueTask>))
        {
            return onRetry;
        }

        if (type == typeof(Action))
        {
            return new Action(() => operation(CancellationToken.None).GetAwaiter().GetResult());
        }

        if (type == typeof(Func<string>))
        {
            return new Func<string>(() => operation(CancellationToken.None).GetAwaiter().GetResult());
        }

        if (type == typeof(Func<Task>))
        {
            return new Func<Task>(() => operation(CancellationToken.None));
        }

        if (type == typeof(Func<Task<string>>))
        {
            return new Func<Task<string>>(() => operation(CancellationToken.None));
        }

        if (type == typeof(Func<CancellationToken, Task>))
        {
            return new Func<CancellationToken, Task>(ct => operation(ct));
        }

        if (type == typeof(Func<CancellationToken, Task<string>>))
        {
            return operation;
        }

        throw new NotSupportedException($"No argument is defined for a parameter of type {type}.");
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
