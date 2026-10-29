// <copyright file="PipelineOperationDriver.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Makes pipeline operations of each <see cref="OperationShape"/> from a synchronous body, which throws for a failure
/// or returns for a success.
/// </summary>
internal static class PipelineOperationDriver
{
    /// <summary>
    /// Makes an operation that returns no result.
    /// </summary>
    /// <param name="shape">The shape in which the operation gives the outcome of the body.</param>
    /// <param name="body">The body of each attempt. It throws for a failure.</param>
    /// <returns>The operation.</returns>
    public static Func<CancellationToken, ValueTask> WithoutResult(OperationShape shape, Action body)
    {
        return shape switch
        {
            OperationShape.Synchronous => _ =>
            {
                body();
                return ValueTask.CompletedTask;
            },
            OperationShape.CompletedValueTask => _ =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    return ValueTask.FromException(ex);
                }

                return ValueTask.CompletedTask;
            },
            OperationShape.AsyncBeforeAwait => _ => BeforeAwaitAsync(body),
            OperationShape.AsyncAfterAwait => _ => AfterAwaitAsync(body),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>
    /// Makes an operation that returns a result.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="shape">The shape in which the operation gives the outcome of the body.</param>
    /// <param name="body">The body of each attempt. It throws for a failure, and returns the result for a success.</param>
    /// <returns>The operation.</returns>
    public static Func<CancellationToken, ValueTask<T>> WithResult<T>(OperationShape shape, Func<T> body)
    {
        return shape switch
        {
            OperationShape.Synchronous => _ => new ValueTask<T>(body()),
            OperationShape.CompletedValueTask => _ =>
            {
                T result;
                try
                {
                    result = body();
                }
                catch (Exception ex)
                {
                    return ValueTask.FromException<T>(ex);
                }

                return ValueTask.FromResult(result);
            },
            OperationShape.AsyncBeforeAwait => _ => BeforeAwaitAsync(body),
            OperationShape.AsyncAfterAwait => _ => AfterAwaitAsync(body),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

#pragma warning disable CS1998 // The method has no await, so that it completes before its first await.
    private static async ValueTask BeforeAwaitAsync(Action body)
    {
        body();
    }

    private static async ValueTask<T> BeforeAwaitAsync<T>(Func<T> body)
    {
        return body();
    }
#pragma warning restore CS1998

    private static async ValueTask AfterAwaitAsync(Action body)
    {
        await Task.Yield();
        body();
    }

    private static async ValueTask<T> AfterAwaitAsync<T>(Func<T> body)
    {
        await Task.Yield();
        return body();
    }
}
