// <copyright file="RetryTaskFactoryDriver.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// Calls the exact public <c>StartNew</c> overload of <see cref="RetryTaskFactory"/> or <see cref="RetryTaskFactory{T}"/>
    /// identified by a (<see cref="StartNewOverload"/>, <see cref="StartNewDefaults"/>) pair, so that a behaviour can be
    /// asserted across every overload with <c>[DynamicData]</c>.
    /// </summary>
    /// <remarks>
    /// Overloads that take no state run the operation with a <see langword="null"/> state. Overloads that take no
    /// cancellation token ignore the token. Overloads that take creation options and a scheduler are passed
    /// <see cref="TaskCreationOptions.None"/> and <see cref="TaskScheduler.Default"/>.
    /// </remarks>
    public static class RetryTaskFactoryDriver
    {
        /// <summary>
        /// Gets every (overload, defaults) pair that exists as a public <c>StartNew</c> method on each factory.
        /// </summary>
        public static IReadOnlyList<(StartNewOverload Overload, StartNewDefaults Defaults)> AllOverloads { get; } =
            (from StartNewDefaults defaults in Enum.GetValues(typeof(StartNewDefaults))
             from StartNewOverload overload in Enum.GetValues(typeof(StartNewOverload))
             where Exists(overload, defaults)
             select (overload, defaults)).ToList();

        public static IEnumerable<object[]> All => Select(_ => true);

        public static IEnumerable<object[]> WithStrategy => Select(o => TakesStrategy(o.Defaults));

        public static IEnumerable<object[]> WithPolicy => Select(o => TakesPolicy(o.Defaults));

        public static IEnumerable<object[]> WithoutStrategy => Select(o => !TakesStrategy(o.Defaults));

        public static IEnumerable<object[]> WithStrategyOnly => Select(o => o.Defaults == StartNewDefaults.StrategyOnly);

        public static IEnumerable<object[]> WithState => Select(o => TakesState(o.Overload));

        public static IEnumerable<object[]> WithCancellationToken => Select(o => TakesCancellationToken(o.Overload));

        public static bool TakesStrategy(StartNewDefaults defaults) => defaults is StartNewDefaults.StrategyAndPolicy or StartNewDefaults.StrategyOnly;

        public static bool TakesPolicy(StartNewDefaults defaults) => defaults is StartNewDefaults.StrategyAndPolicy or StartNewDefaults.PolicyOnly;

        public static bool TakesState(StartNewOverload overload) =>
            overload is StartNewOverload.WithState
                or StartNewOverload.WithStateAndCreationOptions
                or StartNewOverload.WithStateAndCancellationToken
                or StartNewOverload.WithStateCancellationTokenOptionsAndScheduler;

        public static bool TakesCancellationToken(StartNewOverload overload) =>
            overload is StartNewOverload.WithCancellationToken
                or StartNewOverload.WithStateAndCancellationToken
                or StartNewOverload.WithStateCancellationTokenOptionsAndScheduler
                or StartNewOverload.WithCancellationTokenOptionsAndScheduler;

        /// <summary>
        /// Calls one <see cref="RetryTaskFactory"/>.StartNew overload.
        /// </summary>
        /// <param name="factory">The factory.</param>
        /// <param name="overload">The task-related parameters of the overload.</param>
        /// <param name="defaults">Which of strategy and policy the overload takes.</param>
        /// <param name="action">The operation; receives the state, or null for overloads without state.</param>
        /// <param name="state">The state, passed only to overloads that take it.</param>
        /// <param name="cancellationToken">The token, passed only to overloads that take it.</param>
        /// <param name="strategy">The strategy; required when <paramref name="defaults"/> takes one.</param>
        /// <param name="policy">The policy; required when <paramref name="defaults"/> takes one.</param>
        /// <returns>The task returned by the factory.</returns>
        public static Task StartNew(
            RetryTaskFactory factory,
            StartNewOverload overload,
            StartNewDefaults defaults,
            Action<object?> action,
            object? state,
            CancellationToken cancellationToken,
            IRetryStrategy? strategy,
            IRetryPolicy? policy)
        {
            Action plain = () => action(null);
            CancellationToken ct = cancellationToken;
            const TaskCreationOptions options = TaskCreationOptions.None;
            TaskScheduler scheduler = TaskScheduler.Default;
            IRetryStrategy s = RequireIf(TakesStrategy(defaults), strategy);
            IRetryPolicy p = RequireIf(TakesPolicy(defaults), policy);

            return (defaults, overload) switch
            {
                (StartNewDefaults.Neither, StartNewOverload.Plain) => factory.StartNew(plain),
                (StartNewDefaults.Neither, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct),
                (StartNewDefaults.Neither, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options),
                (StartNewDefaults.Neither, StartNewOverload.WithState) => factory.StartNew(action, state),
                (StartNewDefaults.Neither, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(action, state, options),
                (StartNewDefaults.Neither, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(action, state, ct, options, scheduler),
                (StartNewDefaults.Neither, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler),

                (StartNewDefaults.PolicyOnly, StartNewOverload.Plain) => factory.StartNew(plain, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithState) => factory.StartNew(action, state, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(action, state, options, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(action, state, ct, options, scheduler, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, p),

                (StartNewDefaults.StrategyOnly, StartNewOverload.Plain) => factory.StartNew(plain, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithState) => factory.StartNew(action, state, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(action, state, options, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(action, state, ct, options, scheduler, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, s),

                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.Plain) => factory.StartNew(plain, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithState) => factory.StartNew(action, state, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(action, state, options, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateAndCancellationToken) => factory.StartNew(action, state, ct, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(action, state, ct, options, scheduler, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, s, p),

                _ => throw new ArgumentOutOfRangeException(nameof(overload), $"RetryTaskFactory has no StartNew overload for ({overload}, {defaults})."),
            };
        }

        /// <summary>
        /// Calls one <see cref="RetryTaskFactory{T}"/>.StartNew overload.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="factory">The factory.</param>
        /// <param name="overload">The task-related parameters of the overload.</param>
        /// <param name="defaults">Which of strategy and policy the overload takes.</param>
        /// <param name="function">The operation; receives the state, or null for overloads without state.</param>
        /// <param name="state">The state, passed only to overloads that take it.</param>
        /// <param name="cancellationToken">The token, passed only to overloads that take it.</param>
        /// <param name="strategy">The strategy; required when <paramref name="defaults"/> takes one.</param>
        /// <param name="policy">The policy; required when <paramref name="defaults"/> takes one.</param>
        /// <returns>The task returned by the factory.</returns>
        public static Task<T> StartNew<T>(
            RetryTaskFactory<T> factory,
            StartNewOverload overload,
            StartNewDefaults defaults,
            Func<object?, T> function,
            object? state,
            CancellationToken cancellationToken,
            IRetryStrategy? strategy,
            IRetryPolicy? policy)
        {
            Func<T> plain = () => function(null);
            CancellationToken ct = cancellationToken;
            const TaskCreationOptions options = TaskCreationOptions.None;
            TaskScheduler scheduler = TaskScheduler.Default;
            IRetryStrategy s = RequireIf(TakesStrategy(defaults), strategy);
            IRetryPolicy p = RequireIf(TakesPolicy(defaults), policy);

            return (defaults, overload) switch
            {
                (StartNewDefaults.Neither, StartNewOverload.Plain) => factory.StartNew(plain),
                (StartNewDefaults.Neither, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct),
                (StartNewDefaults.Neither, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options),
                (StartNewDefaults.Neither, StartNewOverload.WithState) => factory.StartNew(function, state),
                (StartNewDefaults.Neither, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(function, state, options),
                (StartNewDefaults.Neither, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(function, state, ct, options, scheduler),
                (StartNewDefaults.Neither, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler),

                (StartNewDefaults.PolicyOnly, StartNewOverload.Plain) => factory.StartNew(plain, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithState) => factory.StartNew(function, state, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(function, state, options, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(function, state, ct, options, scheduler, p),
                (StartNewDefaults.PolicyOnly, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, p),

                (StartNewDefaults.StrategyOnly, StartNewOverload.Plain) => factory.StartNew(plain, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithState) => factory.StartNew(function, state, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(function, state, options, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(function, state, ct, options, scheduler, s),
                (StartNewDefaults.StrategyOnly, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, s),

                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.Plain) => factory.StartNew(plain, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCancellationToken) => factory.StartNew(plain, ct, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCreationOptions) => factory.StartNew(plain, options, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithState) => factory.StartNew(function, state, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateAndCreationOptions) => factory.StartNew(function, state, options, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateAndCancellationToken) => factory.StartNew(function, state, ct, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithStateCancellationTokenOptionsAndScheduler) => factory.StartNew(function, state, ct, options, scheduler, s, p),
                (StartNewDefaults.StrategyAndPolicy, StartNewOverload.WithCancellationTokenOptionsAndScheduler) => factory.StartNew(plain, ct, options, scheduler, s, p),

                _ => throw new ArgumentOutOfRangeException(nameof(overload), $"RetryTaskFactory<T> has no StartNew overload for ({overload}, {defaults})."),
            };
        }

        private static bool Exists(StartNewOverload overload, StartNewDefaults defaults)
        {
            // Only the strategy-and-policy family has a (state, cancellationToken) overload.
            return overload != StartNewOverload.WithStateAndCancellationToken || defaults == StartNewDefaults.StrategyAndPolicy;
        }

        private static IEnumerable<object[]> Select(Func<(StartNewOverload Overload, StartNewDefaults Defaults), bool> predicate)
        {
            return AllOverloads.Where(predicate).Select(o => new object[] { o.Overload, o.Defaults });
        }

        private static TArgument RequireIf<TArgument>(bool required, TArgument? argument)
            where TArgument : class
        {
            // Overloads that do not take the argument never read it, so a placeholder is never observed.
            return required
                ? argument ?? throw new ArgumentNullException(nameof(argument), "This StartNew overload needs this argument.")
                : argument!;
        }
    }
}
