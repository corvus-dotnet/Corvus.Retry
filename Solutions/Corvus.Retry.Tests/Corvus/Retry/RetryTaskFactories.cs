// <copyright file="RetryTaskFactories.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

/// <summary>
/// Calls a <c>StartNew</c> overload of either factory through <see cref="RetryTaskFactoryDriver"/>, so that a test can
/// assert a behaviour of both factories with one body.
/// </summary>
internal static class RetryTaskFactories
{
    /// <summary>
    /// The result of the function that <c>StartNew</c> passes to <see cref="RetryTaskFactory{T}"/>.
    /// </summary>
    public const string Result = "done";

    /// <summary>
    /// Adds the factory as the first value of each <c>[DynamicData]</c> row, once for each factory.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The rows for both factories.</returns>
    public static IEnumerable<object[]> ForBoth(IEnumerable<object[]> rows)
    {
        return from RetryTaskFactoryKind factory in Enum.GetValues<RetryTaskFactoryKind>()
               from object[] row in rows
               select new object[] { factory }.Concat(row).ToArray();
    }

    /// <summary>
    /// Calls one <c>StartNew</c> overload of a new factory of the given kind.
    /// </summary>
    /// <param name="factory">The kind of factory.</param>
    /// <param name="overload">The task-related parameters of the overload.</param>
    /// <param name="defaults">Which of strategy and policy the overload takes.</param>
    /// <param name="action">The operation. For <see cref="RetryTaskFactory{T}"/>, a function runs it and returns <see cref="Result"/>.</param>
    /// <param name="state">The state, passed only to overloads that take it.</param>
    /// <param name="strategy">The strategy, passed only to overloads that take one. The default is <c>new Count(5)</c>.</param>
    /// <param name="policy">The policy, passed only to overloads that take one. The default is <see cref="AnyExceptionPolicy"/>.</param>
    /// <param name="cancellationToken">The token, passed only to overloads that take it.</param>
    /// <param name="options">The creation options, passed only to overloads that take them.</param>
    /// <param name="scheduler">The scheduler, passed only to overloads that take one.</param>
    /// <returns>The task returned by the factory.</returns>
    public static Task StartNew(
        RetryTaskFactoryKind factory,
        StartNewOverload overload,
        StartNewDefaults defaults,
        Action<object?> action,
        object? state = null,
        IRetryStrategy? strategy = null,
        IRetryPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TaskCreationOptions options = TaskCreationOptions.None,
        TaskScheduler? scheduler = null)
    {
        strategy ??= new Count(5);
        policy ??= new AnyExceptionPolicy();

        return factory == RetryTaskFactoryKind.RetryTaskFactory
            ? RetryTaskFactoryDriver.StartNew(new RetryTaskFactory(), overload, defaults, action, state, cancellationToken, strategy, policy, options, scheduler)
            : RetryTaskFactoryDriver.StartNew(
                new RetryTaskFactory<string>(),
                overload,
                defaults,
                s =>
                {
                    action(s);
                    return Result;
                },
                state,
                cancellationToken,
                strategy,
                policy,
                options,
                scheduler);
    }

    /// <summary>
    /// Calls the <c>StartNew</c> overload that takes a cancellation token, a strategy and a policy.
    /// </summary>
    /// <param name="factory">The kind of factory.</param>
    /// <param name="action">The operation.</param>
    /// <param name="strategy">The strategy.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <returns>The task returned by the factory.</returns>
    public static Task StartNew(RetryTaskFactoryKind factory, Action action, IRetryStrategy strategy, IRetryPolicy policy, CancellationToken cancellationToken = default)
    {
        return StartNew(factory, StartNewOverload.WithCancellationToken, StartNewDefaults.StrategyAndPolicy, _ => action(), null, strategy, policy, cancellationToken);
    }
}
