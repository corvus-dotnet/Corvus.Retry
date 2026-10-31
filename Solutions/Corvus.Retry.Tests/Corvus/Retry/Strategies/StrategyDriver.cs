// <copyright file="StrategyDriver.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Strategies;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Drives an <see cref="IRetryStrategy"/> through a series of failed attempts, as a retry loop would.
/// </summary>
internal static class StrategyDriver
{
    /// <summary>
    /// Asks the strategy for the delay after each of a number of failed attempts.
    /// </summary>
    /// <param name="strategy">The strategy.</param>
    /// <param name="failures">The number of failed attempts to report.</param>
    /// <returns>
    /// The delay the strategy returned after each failed attempt, starting with attempt 1; <see langword="null"/>
    /// where it refused a retry.
    /// </returns>
    /// <remarks>
    /// The <see cref="RetryAttempt.PreviousDelay"/> of each attempt is the last delay that was not
    /// <see langword="null"/>, and <see cref="TimeSpan.Zero"/> for the first attempt.
    /// </remarks>
    public static List<TimeSpan?> FailTimes(IRetryStrategy strategy, int failures)
    {
        return FailTimes(strategy, failures, attempt => new InvalidOperationException($"Failure {attempt}"));
    }

    /// <summary>
    /// Asks the strategy for the delay after each of a number of failed attempts.
    /// </summary>
    /// <param name="strategy">The strategy.</param>
    /// <param name="failures">The number of failed attempts to report.</param>
    /// <param name="exceptionFactory">Creates the exception of each failed attempt, from its attempt number.</param>
    /// <returns>
    /// The delay the strategy returned after each failed attempt, starting with attempt 1; <see langword="null"/>
    /// where it refused a retry.
    /// </returns>
    public static List<TimeSpan?> FailTimes(IRetryStrategy strategy, int failures, Func<int, Exception> exceptionFactory)
    {
        List<TimeSpan?> delays = [];
        TimeSpan previousDelay = TimeSpan.Zero;
        for (int attempt = 1; attempt <= failures; attempt++)
        {
            TimeSpan? delay = strategy.GetRetryDelay(new RetryAttempt(attempt, exceptionFactory(attempt), null, previousDelay));
            delays.Add(delay);
            if (delay is TimeSpan next)
            {
                previousDelay = next;
            }
        }

        return delays;
    }

    /// <summary>
    /// Drives one strategy instance from several threads at the same time, each as a separate call.
    /// </summary>
    /// <param name="strategy">The strategy.</param>
    /// <param name="failures">The number of failed attempts each thread reports.</param>
    /// <param name="threads">The number of threads.</param>
    /// <param name="repetitions">The number of sequences each thread drives.</param>
    /// <returns>Every sequence of delays that every thread saw.</returns>
    public static async Task<List<List<TimeSpan?>>> FailTimesConcurrentlyAsync(IRetryStrategy strategy, int failures, int threads = 4, int repetitions = 250)
    {
        using Barrier start = new(threads);
        var tasks = new Task<List<List<TimeSpan?>>>[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait(TimeSpan.FromSeconds(10));
                    List<List<TimeSpan?>> sequences = [];
                    for (int r = 0; r < repetitions; r++)
                    {
                        sequences.Add(FailTimes(strategy, failures));
                    }

                    return sequences;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        List<List<TimeSpan?>>[] results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        List<List<TimeSpan?>> all = [];
        foreach (List<List<TimeSpan?>> result in results)
        {
            all.AddRange(result);
        }

        return all;
    }
}
