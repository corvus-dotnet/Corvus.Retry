// <copyright file="ReliableTaskRunner.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;

/// <summary>
/// A host for a long-running task within a process that will restart if it fails, according to a <see cref="IRetryPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// There are three different ways of retrying tasks in this library.
/// <list type="number">
/// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
/// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
/// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ReliableTaskRunner
{
    private readonly CancellationTokenSource cancellationTokenSource;

    // Guards processingTask and stopping, so that StopAsync and a restart cannot interleave.
    private readonly Lock sync = new();
    private Task processingTask;
    private bool stopping;

    /// <summary>
    /// In order to ensure that processingTask has been initialised, we need to invoke runFunction before returning.
    /// This is more work than we would normally do in a constructor, but as the constructor is private we think this
    /// is tolerable.
    /// </summary>
    private ReliableTaskRunner(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        this.cancellationTokenSource = new CancellationTokenSource();

        this.processingTask = runFunction(this.cancellationTokenSource.Token);
        this.AttachFailureContinuation(this.processingTask, runFunction, retryPolicy);
    }

    /// <summary>
    /// Run a cancellable function until cancellation.
    /// </summary>
    /// <param name="runFunction">The function to run until cancellation.</param>
    /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
    public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction)
    {
        ArgumentNullException.ThrowIfNull(runFunction);

        return Run(runFunction, AnyExceptionPolicy.Default);
    }

    /// <summary>
    /// Run a cancellable function until cancellation.
    /// </summary>
    /// <param name="runFunction">The function to run until cancellation.</param>
    /// <param name="retryPolicy">The retry policy used to control whether the task will be restarted on failure.</param>
    /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
    public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(runFunction);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        return new ReliableTaskRunner(runFunction, retryPolicy);
    }

    /// <summary>
    /// Stops a running function.
    /// </summary>
    /// <returns>A task which completes when the function terminates.</returns>
    public Task StopAsync()
    {
        Task task;
        lock (this.sync)
        {
            // Once this is set no restart can begin, so the task we return is the last run there will be.
            this.stopping = true;
            task = this.processingTask;
        }

        // Cancel outside the lock, so that callbacks registered on the token do not run while we hold it.
        this.cancellationTokenSource.Cancel();
        return task;
    }

    private static OperationCanceledException GetCancellation(Task cancelledTask)
    {
        try
        {
            // Rethrows the OperationCanceledException that cancelled the task, where the runtime has kept it.
            cancelledTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }

        return new TaskCanceledException(cancelledTask);
    }

    private void Restart(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        // Publish a stand-in for the new run before starting it, so that a StopAsync call made while runFunction
        // is still executing gets a task that represents the new run rather than the faulted one it replaces.
        var nextRun = new TaskCompletionSource<Task>();
        Task published;
        lock (this.sync)
        {
            if (this.stopping)
            {
                return;
            }

            published = nextRun.Task.Unwrap();
            this.processingTask = published;
        }

        Task task;
        try
        {
            task = runFunction(this.cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            // There is no caller to throw to on a restart, so treat this like any other failed run.
            task = Task.FromException(ex);
        }

        nextRun.SetResult(task);

        // Watch the published stand-in rather than the run's own task. Unwrap observes the run's failure, and
        // the continuation observes the stand-in's, so a run that is replaced by a further restart does not
        // report its failure as unobserved.
        this.AttachFailureContinuation(published, runFunction, retryPolicy);
    }

    private void AttachFailureContinuation(Task task, Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        // The continuation is cancelled when StopAsync cancels the token, so it only runs for a failure, or a
        // cancellation, that the operation brought on itself (for example an HttpClient timeout).
        task.ContinueWith(
        t =>
        {
            bool canRetry;
            if (t.Exception is AggregateException ex)
            {
                // Consult the policy with the exceptions the operation actually threw, not the AggregateException
                // wrapper, as RetryTask does.
                canRetry = retryPolicy.CanRetryAll(ex);
            }
            else
            {
                canRetry = retryPolicy.CanRetry(GetCancellation(t));
            }

            if (canRetry)
            {
                // Run again if we were allowed to
                this.Restart(runFunction, retryPolicy);
            }
        },
        this.cancellationTokenSource.Token,
        TaskContinuationOptions.NotOnRanToCompletion,
        TaskScheduler.Current);
    }
}