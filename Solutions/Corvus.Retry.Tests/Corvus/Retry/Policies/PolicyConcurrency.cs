// <copyright file="PolicyConcurrency.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Uses a policy from many threads at the same time, to show that it gives consistent answers.
/// </summary>
internal static class PolicyConcurrency
{
    /// <summary>
    /// Runs a check on several threads at the same time, many times on each, and asserts that it always passes.
    /// </summary>
    /// <param name="check">The check. It returns true when the policy gave the expected answers.</param>
    /// <param name="threads">The number of threads.</param>
    /// <param name="repetitions">The number of checks on each thread.</param>
    /// <returns>A task that completes when every thread has finished.</returns>
    public static async Task RunAsync(Func<bool> check, int threads = 8, int repetitions = 1000)
    {
        using Barrier start = new(threads);
        var tasks = new Task<int>[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait(TimeSpan.FromSeconds(10));
                    int failures = 0;
                    for (int r = 0; r < repetitions; r++)
                    {
                        if (!check())
                        {
                            failures++;
                        }
                    }

                    return failures;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        int[] results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        foreach (int failures in results)
        {
            Assert.AreEqual(0, failures);
        }
    }
}
