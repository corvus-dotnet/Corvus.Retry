// <copyright file="ReliableTaskRunnerBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Corvus.Retry.Policies;

/// <summary>
/// The cost of hosting an operation with <see cref="ReliableTaskRunner"/>: starting it, restarting it after
/// failures, and stopping it.
/// </summary>
/// <remarks>
/// The operation runs until the runner's token is cancelled, so each benchmark measures a complete start-to-stop
/// cycle. No benchmark waits for time to pass.
/// </remarks>
[MemoryDiagnoser]
public class ReliableTaskRunnerBenchmarks
{
    private static readonly Func<CancellationToken, Task> RunUntilStopped = WaitForCancellationAsync;

    private readonly AnyExceptionPolicy policy = new();

    [Benchmark(Baseline = true)]
    public Task Run_StopAsync()
    {
        return ReliableTaskRunner.Run(RunUntilStopped).StopAsync();
    }

    [Benchmark]
    public Task Run_WithPolicy_StopAsync()
    {
        return ReliableTaskRunner.Run(RunUntilStopped, this.policy).StopAsync();
    }

    /// <summary>
    /// Starts an operation that fails, and is restarted, a number of times before it runs until stopped.
    /// </summary>
    /// <param name="restarts">The number of times the operation fails and is restarted.</param>
    /// <returns>A task that completes when the runner has stopped.</returns>
    [Benchmark]
    [Arguments(1)]
    [Arguments(4)]
    public async Task Run_RestartAfterFailures_StopAsync(int restarts)
    {
        int remainingFailures = restarts;
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ReliableTaskRunner runner = ReliableTaskRunner.Run(
            ct =>
            {
                if (remainingFailures-- > 0)
                {
                    return Task.FromException(new InvalidOperationException("Transient failure"));
                }

                running.TrySetResult();
                return WaitForCancellationAsync(ct);
            },
            this.policy);

        await running.Task.ConfigureAwait(false);
        await runner.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a task that completes, without throwing, when the token is cancelled.
    /// </summary>
    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(static c => ((TaskCompletionSource)c!).TrySetResult(), cancelled);
        return cancelled.Task;
    }
}
