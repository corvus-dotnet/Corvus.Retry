// <copyright file="DeferredValueTask.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

/// <summary>
/// Makes value tasks that are not complete until something awaits them, and that then complete on a thread-pool thread
/// with no <see cref="SynchronizationContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// An await of such a value task always yields, and the continuation never runs inline on the thread of the code that
/// awaited. The continuation runs on the context that the await captured, or on the thread pool if the await did not
/// capture a context. So a test can tell from <see cref="SynchronizationContext.Current"/> after the await whether the
/// await used <c>ConfigureAwait(true)</c> or <c>ConfigureAwait(false)</c>, with no race between the completion and the
/// await.
/// </para>
/// <para>
/// Each value task can be awaited one time only.
/// </para>
/// </remarks>
internal static class DeferredValueTask
{
    /// <summary>
    /// Makes a value task that completes successfully after something awaits it.
    /// </summary>
    /// <returns>The value task.</returns>
    public static ValueTask Completed()
    {
        var source = new Source<bool>(true, null, null);
        return new ValueTask(source, source.Version);
    }

    /// <summary>
    /// Makes a value task that completes with a result after something awaits it.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="result">The result.</param>
    /// <returns>The value task.</returns>
    public static ValueTask<T> FromResult<T>(T result)
    {
        return FromResult(result, null);
    }

    /// <summary>
    /// Makes a value task that completes with a result after something awaits it and a gate completes.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="gate">
    /// A task that must complete before the value task completes, or <see langword="null"/> for no gate.
    /// </param>
    /// <returns>The value task.</returns>
    public static ValueTask<T> FromResult<T>(T result, Task? gate)
    {
        var source = new Source<T>(result, null, gate);
        return new ValueTask<T>(source, source.Version);
    }

    /// <summary>
    /// Makes a value task that faults with an exception after something awaits it.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="exception">The exception.</param>
    /// <param name="gate">
    /// A task that must complete before the value task completes, or <see langword="null"/> for no gate.
    /// </param>
    /// <returns>The value task.</returns>
    public static ValueTask<T> FromException<T>(Exception exception, Task? gate = null)
    {
        var source = new Source<T>(default!, exception, gate);
        return new ValueTask<T>(source, source.Version);
    }

    /// <summary>
    /// Makes a value task that faults with an exception after something awaits it and a gate completes.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="gate">A task that must complete before the value task completes.</param>
    /// <returns>The value task.</returns>
    /// <remarks>
    /// Use the gate when code between the await and the code under test can complete the value task first; for
    /// example an adapter that awaits the value task inside another <see langword="async"/> method.
    /// </remarks>
    public static ValueTask FromException(Exception exception, Task gate)
    {
        var source = new Source<bool>(default, exception, gate);
        return new ValueTask(source, source.Version);
    }

    private sealed class Source<T> : IValueTaskSource<T>, IValueTaskSource
    {
        private readonly T result;
        private readonly Exception? exception;
        private readonly Task? gate;
        private ManualResetValueTaskSourceCore<T> core = new() { RunContinuationsAsynchronously = true };

        public Source(T result, Exception? exception, Task? gate)
        {
            this.result = result;
            this.exception = exception;
            this.gate = gate;
        }

        public short Version => this.core.Version;

        public T GetResult(short token) => this.core.GetResult(token);

        void IValueTaskSource.GetResult(short token) => this.core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => this.core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            this.core.OnCompleted(continuation, state, token, flags);

            // Complete only now that the continuation is registered, and on a thread with no context.
            if (this.gate is null)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.Complete(), this, preferLocal: false);
            }
            else
            {
                this.gate.ContinueWith(
                    static (_, s) => ((Source<T>)s!).Complete(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.RunContinuationsAsynchronously,
                    TaskScheduler.Default);
            }
        }

        private void Complete()
        {
            if (this.exception is not null)
            {
                this.core.SetException(this.exception);
            }
            else
            {
                this.core.SetResult(this.result);
            }
        }
    }
}
