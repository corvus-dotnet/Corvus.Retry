// <copyright file="ReliableTaskRunner.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

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
/// <para>
/// Restarts run on the thread pool.
/// </para>
/// </remarks>
public sealed class ReliableTaskRunner : IAsyncDisposable
{
    private const string NullTaskMessage = "The run function returned a null task.";

    private readonly CancellationTokenSource cancellationTokenSource;

    // A copy of the source's token, which stays usable after DisposeAsync disposes the source.
    private readonly CancellationToken cancellationToken;

    private readonly Func<CancellationToken, Task> runFunction;
    private readonly IRetryStrategy? restartStrategy;
    private readonly IRetryPolicy retryPolicy;

    // Guards processingTask, restartDecision, stopping and disposed, so that StopAsync, DisposeAsync and a restart
    // cannot interleave.
    private readonly Lock sync = new();
    private Task processingTask;

    // Completes when the runner has handled the end of the current run: it has decided not to restart, or it has
    // waited for the restart delay and started the next run. It never faults.
    private Task restartDecision = Task.CompletedTask;
    private bool stopping;
    private bool disposed;

    // Only the restart decisions use these. A decision for a run happens before the next run starts, so the decisions
    // never run at the same time.
    private int failedRuns;
    private TimeSpan previousDelay;

    /// <summary>
    /// In order to ensure that processingTask has been initialised, we need to invoke runFunction before returning.
    /// This is more work than we would normally do in a constructor, but as the constructor is private we think this
    /// is tolerable.
    /// </summary>
    private ReliableTaskRunner(Func<CancellationToken, Task> runFunction, IRetryStrategy? restartStrategy, IRetryPolicy retryPolicy)
    {
        this.cancellationTokenSource = new CancellationTokenSource();
        this.cancellationToken = this.cancellationTokenSource.Token;
        this.runFunction = runFunction;
        this.restartStrategy = restartStrategy;
        this.retryPolicy = retryPolicy;

        this.processingTask = runFunction(this.cancellationToken) ?? throw new InvalidOperationException(NullTaskMessage);
        lock (this.sync)
        {
            this.restartDecision = this.WatchForFailure(this.processingTask);
        }
    }

    /// <summary>
    /// Run a cancellable function until cancellation.
    /// </summary>
    /// <param name="runFunction">The function to run until cancellation.</param>
    /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="runFunction"/> returned a null task.</exception>
    /// <remarks>
    /// The runner restarts the function at once after each failure, with no limit. To wait between restarts, use
    /// <see cref="Run(Func{CancellationToken, Task}, IRetryStrategy, IRetryPolicy)"/>.
    /// </remarks>
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
    /// <exception cref="InvalidOperationException"><paramref name="runFunction"/> returned a null task.</exception>
    /// <remarks>
    /// The runner restarts the function at once after each failure that the policy accepts, with no limit. To wait
    /// between restarts, use <see cref="Run(Func{CancellationToken, Task}, IRetryStrategy, IRetryPolicy)"/>.
    /// </remarks>
    public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction, IRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(runFunction);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        return new ReliableTaskRunner(runFunction, null, retryPolicy);
    }

    /// <summary>
    /// Run a cancellable function until cancellation, and wait for the delay of a restart strategy before each restart.
    /// </summary>
    /// <param name="runFunction">The function to run until cancellation.</param>
    /// <param name="restartStrategy">
    /// The strategy that gives the delay before each restart. When it returns <see langword="null"/>, the runner does
    /// not restart.
    /// </param>
    /// <param name="retryPolicy">The retry policy used to control whether the task will be restarted on failure.</param>
    /// <returns>An instance of a runner which can be used to control the long-running function.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="runFunction"/> returned a null task.</exception>
    /// <remarks>
    /// <para>
    /// After a run fails, the runner first gives the strategy a <see cref="RetryAttempt"/>. Its
    /// <see cref="RetryAttempt.AttemptNumber"/> is the number of runs that have failed since the call to this method,
    /// starting at 1. Its <see cref="RetryAttempt.Exception"/> is the <see cref="AggregateException"/> of the failed
    /// run, or the <see cref="OperationCanceledException"/> of a run that cancelled itself. If the strategy returns
    /// <see langword="null"/>, the runner does not restart. Otherwise the policy decides whether to restart. If it
    /// does, the runner waits for the delay, and then starts the next run.
    /// </para>
    /// <para>
    /// A bounded strategy stops the runner after that many failed runs. Use a strategy with no limit, for example
    /// <c>new Exponential(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))</c>.
    /// </para>
    /// <para>
    /// <see cref="StopAsync"/> and <see cref="DisposeAsync"/> stop a restart delay, and no restart occurs.
    /// </para>
    /// </remarks>
    public static ReliableTaskRunner Run(Func<CancellationToken, Task> runFunction, IRetryStrategy restartStrategy, IRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(runFunction);
        ArgumentNullException.ThrowIfNull(restartStrategy);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        return new ReliableTaskRunner(runFunction, restartStrategy, retryPolicy);
    }

    /// <summary>
    /// Stops a running function.
    /// </summary>
    /// <returns>
    /// A task which completes when the function terminates. It ends as the last run ended: it faults with the
    /// exception of a failed run, is cancelled if the run was cancelled, and otherwise completes. If the policy or the
    /// restart strategy throws, the runner stops and the task faults with that exception. If a callback registered on
    /// the function's cancellation token throws, the task still waits for the last run to end. It then faults with the
    /// exceptions of the callbacks, followed by the exceptions of the run if the run failed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The callbacks registered on the function's cancellation token run on another thread, so this method does not
    /// block while they run.
    /// </para>
    /// <para>
    /// If the runner is deciding whether to restart when this method is called, the task waits for the decision. A
    /// restart delay stops at once, and no restart occurs.
    /// </para>
    /// <para>
    /// Do not await the task from StopAsync inside the run function: it completes only when the run ends.
    /// </para>
    /// <para>
    /// After <see cref="DisposeAsync"/>, this method does not cancel anything. It returns a task that ends as the last
    /// run ended.
    /// </para>
    /// </remarks>
    public Task StopAsync()
    {
        Task cancellation;
        lock (this.sync)
        {
            // Once this is set no restart can begin, so the run we wait for is the last run there will be.
            this.stopping = true;

            // CancelAsync runs the token's callbacks on another thread, so they do not run while we hold the lock.
            // Cancelling under the lock means DisposeAsync cannot dispose the source between the check and the call.
            cancellation = this.disposed ? Task.CompletedTask : this.cancellationTokenSource.CancelAsync();
        }

        // Compose the tasks without await, which would keep only the first exception of a run that faulted with
        // several. Unwrap gives the returned task all the exceptions of the task that the continuation selects.
        // A token callback that throws faults the cancellation while the run can still be going, so wait for the run.
        return cancellation.ContinueWith(
            static (c, state) =>
            {
                Task lastRun = ((ReliableTaskRunner)state!).WhenLastRunEnds();
                return c.IsFaulted ? AddCallbackFailures(c, lastRun) : lastRun;
            },
            this,
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
    /// if the run failed or was cancelled, or a callback registered on the function's cancellation token threw. It
    /// still waits for the last run to end. Call <see cref="StopAsync"/> first if you need the outcome.
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
        Task cancellation;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.stopping = true;
            cancellation = this.cancellationTokenSource.CancelAsync();
        }

        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A token callback failed. StopAsync reports this to callers that need it.
        }

        try
        {
            await this.WhenLastRunEnds().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal only waits for the run to end. StopAsync reports the outcome to callers that need it.
        }

        // The run has ended and no restart can begin, so nothing uses the source any more.
        this.cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Gets a task that ends when the last run ends, and faults with the exceptions of the token callbacks followed by
    /// the exceptions of the run, if the run failed.
    /// </summary>
    /// <param name="cancellation">The task from <see cref="CancellationTokenSource.CancelAsync"/>, which has faulted.</param>
    /// <param name="lastRun">A task that ends as the last run ended.</param>
    /// <returns>A task that faults when the last run ends.</returns>
    private static Task AddCallbackFailures(Task cancellation, Task lastRun)
    {
        return lastRun.ContinueWith(
            static (run, state) =>
            {
                var exceptions = new List<Exception>();
                foreach (Exception exception in ((Task)state!).Exception!.InnerExceptions)
                {
                    // CancelAsync faults with one AggregateException that holds the exceptions of the callbacks.
                    if (exception is AggregateException callbackFailures)
                    {
                        exceptions.AddRange(callbackFailures.InnerExceptions);
                    }
                    else
                    {
                        exceptions.Add(exception);
                    }
                }

                if (run.Exception is AggregateException runFailure)
                {
                    exceptions.AddRange(runFailure.InnerExceptions);
                }

                var stopped = new TaskCompletionSource();
                stopped.SetException(exceptions);
                return stopped.Task;
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
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

    /// <summary>
    /// Gets a task that ends as the last run ended, after the runner has handled the end of the current run.
    /// </summary>
    /// <remarks>Call this only after <see cref="stopping"/> is set, so that no later run can start.</remarks>
    private Task WhenLastRunEnds()
    {
        Task decision;
        lock (this.sync)
        {
            decision = this.restartDecision;
        }

        // A decision in progress can start one more run, or end the runner with an exception from the policy or the
        // strategy. A restart that began before the runner stopped sets a new decision, so check for one.
        return decision.ContinueWith(
            static (d, state) =>
            {
                var runner = (ReliableTaskRunner)state!;
                lock (runner.sync)
                {
                    if (ReferenceEquals(d, runner.restartDecision))
                    {
                        return runner.processingTask;
                    }
                }

                return runner.WhenLastRunEnds();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Starts to watch a run for a failure.
    /// </summary>
    /// <param name="run">The task of the run.</param>
    /// <returns>A task that completes when the runner has handled the end of the run.</returns>
    /// <remarks>Call this with <see cref="sync"/> held, and store the result in <see cref="restartDecision"/>.</remarks>
    private Task WatchForFailure(Task run)
    {
        // The continuation is cancelled when StopAsync or DisposeAsync cancels the token, so it only runs for a
        // failure, or a cancellation, that the operation brought on itself (for example an HttpClient timeout).
        // It runs on the thread pool, not on the scheduler of the caller, which can stop accepting tasks.
        return run.ContinueWith(
            static (t, state) => ((ReliableTaskRunner)state!).OnRunFailed(t),
            this,
            this.cancellationToken,
            TaskContinuationOptions.NotOnRanToCompletion,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Decides whether to restart after a run failed or cancelled itself, and starts the restart.
    /// </summary>
    /// <param name="run">The task of the run.</param>
    /// <returns>A task that completes when the restart has started, or at once if there is no restart.</returns>
    private Task OnRunFailed(Task run)
    {
        try
        {
            // The policy sees the inner exceptions of the run. The strategy sees the run's AggregateException, or the
            // OperationCanceledException of a run that cancelled itself, as RetryTask passes them.
            AggregateException? failure = run.Exception;
            Exception exception = failure is not null ? failure : GetCancellation(run);

            TimeSpan delay = TimeSpan.Zero;
            if (this.restartStrategy is IRetryStrategy strategy)
            {
                if (this.failedRuns < int.MaxValue)
                {
                    this.failedRuns++;
                }

                if (strategy.GetRetryDelay(new RetryAttempt(this.failedRuns, exception, null, this.previousDelay)) is not TimeSpan retryDelay)
                {
                    return Task.CompletedTask;
                }

                delay = RetryDelay.Clamp(retryDelay);
            }

            bool canRetry = failure is not null
                ? this.retryPolicy.CanRetryAll(failure)
                : this.retryPolicy.CanRetry(exception);

            if (!canRetry)
            {
                return Task.CompletedTask;
            }

            this.previousDelay = delay;
            if (delay == TimeSpan.Zero)
            {
                this.Restart();
                return Task.CompletedTask;
            }

            // StopAsync and DisposeAsync cancel the token, which stops the delay. A cancelled task does not report an
            // unobserved exception.
            return RetryDelay.WaitAsync(delay, RetryDefaults.TimeProvider, this.cancellationToken).ContinueWith(
                static (delayTask, state) =>
                {
                    if (delayTask.IsCompletedSuccessfully)
                    {
                        ((ReliableTaskRunner)state!).Restart();
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // The policy or the strategy failed, so the runner stops. StopAsync reports the exception.
            Task failed = Task.FromException(ex);

            // Observe the exception, as for a failed run, so that a runner that is never stopped does not report it
            // as unobserved.
            _ = failed.Exception;
            lock (this.sync)
            {
                this.processingTask = failed;
            }

            return Task.CompletedTask;
        }
    }

    private void Restart()
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
            // A null task is a failed run, so that the policy decides whether to restart it.
            task = this.runFunction(this.cancellationToken) ?? Task.FromException(new InvalidOperationException(NullTaskMessage));
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
        lock (this.sync)
        {
            this.restartDecision = this.WatchForFailure(published);
        }
    }
}
