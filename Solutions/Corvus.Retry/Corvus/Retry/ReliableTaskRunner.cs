// <copyright file="ReliableTaskRunner.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Diagnostics.CodeAnalysis;
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
/// <para>
/// Call <see cref="StopAsync"/> to stop the function and observe how its last run ended. Call <see cref="DisposeAsync"/>
/// (for example with <c>await using</c>) to stop the function, wait for it to end without observing the outcome, and
/// release the resources of the runner.
/// </para>
/// </remarks>
public sealed class ReliableTaskRunner : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource;

    // A copy of the source's token, which stays usable after DisposeAsync disposes the source.
    private readonly CancellationToken cancellationToken;

    // Guards processingTask, stopping and disposed, so that StopAsync, DisposeAsync and a restart cannot interleave.
    private readonly Lock sync = new();
    private Task processingTask;
    private bool stopping;
    private bool disposed;

    /// <summary>
    /// In order to ensure that processingTask has been initialised, we need to invoke runFunction before returning.
    /// This is more work than we would normally do in a constructor, but as the constructor is private we think this
    /// is tolerable.
    /// </summary>
    private ReliableTaskRunner(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        this.cancellationTokenSource = new CancellationTokenSource();
        this.cancellationToken = this.cancellationTokenSource.Token;

        this.processingTask = runFunction(this.cancellationToken);
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
    /// <returns>
    /// A task which completes when the function terminates. It ends as the last run ended: it faults with the
    /// exception of a failed run, is cancelled if the run was cancelled, and otherwise completes.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The callbacks registered on the function's cancellation token run on another thread, so this method does not
    /// block while they run.
    /// </para>
    /// <para>
    /// After <see cref="DisposeAsync"/>, this method does not cancel anything. It returns a task that ends as the last
    /// run ended.
    /// </para>
    /// </remarks>
    public Task StopAsync()
    {
        Task task;
        Task cancellation;
        lock (this.sync)
        {
            // Once this is set no restart can begin, so the task we wait for is the last run there will be.
            this.stopping = true;
            task = this.processingTask;

            // CancelAsync runs the token's callbacks on another thread, so they do not run while we hold the lock.
            // Cancelling under the lock means DisposeAsync cannot dispose the source between the check and the call.
            cancellation = this.disposed ? Task.CompletedTask : this.cancellationTokenSource.CancelAsync();
        }

        // Compose the tasks without await, which would keep only the first exception of a run that faulted with
        // several. Unwrap gives the returned task all the exceptions of the task that the continuation selects.
        return cancellation.ContinueWith(
            static (c, state) => c.IsFaulted ? c : (Task)state!,
            task,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Stops a running function, waits for it to terminate, and releases the resources of the runner.
    /// </summary>
    /// <returns>A task which completes when the function terminates and the runner has released its resources.</returns>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="StopAsync"/>, the returned task does not report how the last run ended: it completes even
    /// if the run failed or was cancelled. Call <see cref="StopAsync"/> first if you need the outcome.
    /// </para>
    /// <para>
    /// Calling this method more than once has no effect after the first call.
    /// </para>
    /// </remarks>
    [SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "DisposeAsync must not throw the outcome of the run. StopAsync reports it to callers that need it.")]
    public async ValueTask DisposeAsync()
    {
        Task task;
        Task cancellation;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.stopping = true;
            task = this.processingTask;
            cancellation = this.cancellationTokenSource.CancelAsync();
        }

        try
        {
            await cancellation.ConfigureAwait(false);
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal only waits for the run to end. StopAsync reports the outcome to callers that need it.
        }

        // The run has ended and no restart can begin, so nothing uses the source any more.
        this.cancellationTokenSource.Dispose();
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
            task = runFunction(this.cancellationToken);
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
        // The continuation is cancelled when StopAsync or DisposeAsync cancels the token, so it only runs for a
        // failure, or a cancellation, that the operation brought on itself (for example an HttpClient timeout).
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
        this.cancellationToken,
        TaskContinuationOptions.NotOnRanToCompletion,
        TaskScheduler.Current);
    }
}