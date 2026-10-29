// <copyright file="RetryOperations.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Adapters that let the engine run each shape of operation that the public API accepts.
/// </summary>
/// <remarks>
/// Each adapter is a readonly struct that holds the caller's delegate. Only <see cref="ResultCheckedOperation{T}"/> can
/// treat a result as a failure.
/// </remarks>
internal static class RetryOperations
{
    // A task that has already completed with a result needs no await in the engine. A task that is still running, or
    // that failed, is wrapped as it is, so that the engine's await observes its outcome with the original stack trace.
    private static ValueTask<T> FromTask<T>(Task<T> task)
    {
        return task.IsCompletedSuccessfully ? new ValueTask<T>(task.Result) : new ValueTask<T>(task);
    }

    private static ValueTask<Unit> FromTask(Task task)
    {
        return task.IsCompletedSuccessfully ? default : AwaitAsync(task);
    }

    // The engine applies its own ConfigureAwait(continueOnCapturedContext) when it awaits the result of this method,
    // so the caller's choice of context still applies to the code that runs after the attempt.
    private static async ValueTask<Unit> AwaitAsync(Task task)
    {
        await task.ConfigureAwait(false);
        return default;
    }

    /// <summary>
    /// Adapts a <see cref="Func{TResult}"/> that returns a <see cref="Task{TResult}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    internal readonly struct TaskOperation<T>(Func<Task<T>> operation) : IRetryOperation<T>
    {
        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(operation());
        }

        /// <inheritdoc/>
        public bool IsFailure(T result)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{TResult}"/> that returns a <see cref="Task"/>.
    /// </summary>
    /// <param name="operation">The operation.</param>
    internal readonly struct TaskUnitOperation(Func<Task> operation) : IRetryOperation<Unit>
    {
        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(operation());
        }

        /// <inheritdoc/>
        public bool IsFailure(Unit result)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{T, TResult}"/> that takes a token and returns a <see cref="Task{TResult}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    internal readonly struct TaskTokenOperation<T>(Func<CancellationToken, Task<T>> operation) : IRetryOperation<T>
    {
        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(operation(cancellationToken));
        }

        /// <inheritdoc/>
        public bool IsFailure(T result)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{T, TResult}"/> that takes a token and returns a <see cref="Task"/>.
    /// </summary>
    /// <param name="operation">The operation.</param>
    internal readonly struct TaskTokenUnitOperation(Func<CancellationToken, Task> operation) : IRetryOperation<Unit>
    {
        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(operation(cancellationToken));
        }

        /// <inheritdoc/>
        public bool IsFailure(Unit result)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{T, TResult}"/> that takes a token and returns a <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    internal readonly struct ValueTaskTokenOperation<T>(Func<CancellationToken, ValueTask<T>> operation) : IRetryOperation<T>
    {
        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return operation(cancellationToken);
        }

        /// <inheritdoc/>
        public bool IsFailure(T result)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{T, TResult}"/> that takes a token and returns a <see cref="ValueTask"/>.
    /// </summary>
    /// <param name="operation">The operation.</param>
    internal readonly struct ValueTaskTokenUnitOperation(Func<CancellationToken, ValueTask> operation) : IRetryOperation<Unit>
    {
        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            ValueTask valueTask = operation(cancellationToken);
            if (valueTask.IsCompletedSuccessfully)
            {
                // Get the result, so that a pooled source behind the value task is released.
                valueTask.GetAwaiter().GetResult();
                return default;
            }

            return AwaitAsync(valueTask);
        }

        /// <inheritdoc/>
        public bool IsFailure(Unit result)
        {
            return false;
        }

        private static async ValueTask<Unit> AwaitAsync(ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return default;
        }
    }

    /// <summary>
    /// Adapts a <see cref="Func{T, TResult}"/> that takes a token and returns a <see cref="ValueTask{TResult}"/>, and
    /// uses a predicate to find the results that are failures.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="isFailure">
    /// The predicate that returns <see langword="true"/> for a result that is a failure, or <see langword="null"/> if
    /// no result is a failure.
    /// </param>
    internal readonly struct ResultCheckedOperation<T>(Func<CancellationToken, ValueTask<T>> operation, Func<T, bool>? isFailure) : IRetryOperation<T>
    {
        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return operation(cancellationToken);
        }

        /// <inheritdoc/>
        public bool IsFailure(T result)
        {
            return isFailure?.Invoke(result) ?? false;
        }
    }

    /// <summary>
    /// Adapts a synchronous <see cref="Func{TResult}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    internal readonly struct FuncOperation<T>(Func<T> operation) : IRetrySyncOperation<T>
    {
        /// <inheritdoc/>
        public T Invoke()
        {
            return operation();
        }
    }

    /// <summary>
    /// Adapts a synchronous <see cref="Action"/>.
    /// </summary>
    /// <param name="operation">The operation.</param>
    internal readonly struct ActionOperation(Action operation) : IRetrySyncOperation<Unit>
    {
        /// <inheritdoc/>
        public Unit Invoke()
        {
            operation();
            return default;
        }
    }
}
