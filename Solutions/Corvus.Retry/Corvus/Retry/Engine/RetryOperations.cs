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
    internal readonly struct TaskOperation<T> : IRetryOperation<T>
    {
        private readonly Func<Task<T>> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskOperation{T}"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public TaskOperation(Func<Task<T>> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(this.operation());
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
    internal readonly struct TaskUnitOperation : IRetryOperation<Unit>
    {
        private readonly Func<Task> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskUnitOperation"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public TaskUnitOperation(Func<Task> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(this.operation());
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
    internal readonly struct TaskTokenOperation<T> : IRetryOperation<T>
    {
        private readonly Func<CancellationToken, Task<T>> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskTokenOperation{T}"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public TaskTokenOperation(Func<CancellationToken, Task<T>> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(this.operation(cancellationToken));
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
    internal readonly struct TaskTokenUnitOperation : IRetryOperation<Unit>
    {
        private readonly Func<CancellationToken, Task> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskTokenUnitOperation"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public TaskTokenUnitOperation(Func<CancellationToken, Task> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            return FromTask(this.operation(cancellationToken));
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
    internal readonly struct ValueTaskTokenOperation<T> : IRetryOperation<T>
    {
        private readonly Func<CancellationToken, ValueTask<T>> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="ValueTaskTokenOperation{T}"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public ValueTaskTokenOperation(Func<CancellationToken, ValueTask<T>> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return this.operation(cancellationToken);
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
    internal readonly struct ValueTaskTokenUnitOperation : IRetryOperation<Unit>
    {
        private readonly Func<CancellationToken, ValueTask> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="ValueTaskTokenUnitOperation"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public ValueTaskTokenUnitOperation(Func<CancellationToken, ValueTask> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public ValueTask<Unit> InvokeAsync(CancellationToken cancellationToken)
        {
            ValueTask valueTask = this.operation(cancellationToken);
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
    internal readonly struct ResultCheckedOperation<T> : IRetryOperation<T>
    {
        private readonly Func<CancellationToken, ValueTask<T>> operation;
        private readonly Func<T, bool>? isFailure;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResultCheckedOperation{T}"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        /// <param name="isFailure">
        /// The predicate that returns <see langword="true"/> for a result that is a failure, or <see langword="null"/>
        /// if no result is a failure.
        /// </param>
        public ResultCheckedOperation(Func<CancellationToken, ValueTask<T>> operation, Func<T, bool>? isFailure)
        {
            this.operation = operation;
            this.isFailure = isFailure;
        }

        /// <inheritdoc/>
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            return this.operation(cancellationToken);
        }

        /// <inheritdoc/>
        public bool IsFailure(T result)
        {
            return this.isFailure?.Invoke(result) ?? false;
        }
    }

    /// <summary>
    /// Adapts a synchronous <see cref="Func{TResult}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    internal readonly struct FuncOperation<T> : IRetrySyncOperation<T>
    {
        private readonly Func<T> operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="FuncOperation{T}"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public FuncOperation(Func<T> operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public T Invoke()
        {
            return this.operation();
        }
    }

    /// <summary>
    /// Adapts a synchronous <see cref="Action"/>.
    /// </summary>
    internal readonly struct ActionOperation : IRetrySyncOperation<Unit>
    {
        private readonly Action operation;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActionOperation"/> struct.
        /// </summary>
        /// <param name="operation">The operation.</param>
        public ActionOperation(Action operation)
        {
            this.operation = operation;
        }

        /// <inheritdoc/>
        public Unit Invoke()
        {
            this.operation();
            return default;
        }
    }
}
