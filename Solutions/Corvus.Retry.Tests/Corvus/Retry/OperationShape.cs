// <copyright file="OperationShape.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The ways in which an operation that returns a <see cref="System.Threading.Tasks.ValueTask"/> or a
/// <see cref="System.Threading.Tasks.ValueTask{TResult}"/> can give its outcome to a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Each shape applies to a failure and to a success. A test that runs every shape with a failure covers a synchronous
/// throw, a faulted value task, and an asynchronous fault; a test that runs every shape with a success covers a
/// synchronous and an asynchronous success.
/// </para>
/// <para>
/// <see cref="PipelineOperationDriver"/> makes an operation of each shape.
/// </para>
/// </remarks>
public enum OperationShape
{
    /// <summary>
    /// The operation throws before it returns, or it returns a value task that holds the result.
    /// </summary>
    Synchronous,

    /// <summary>
    /// The operation returns <c>ValueTask.FromException</c> for a failure, or <c>ValueTask.FromResult</c> for a success.
    /// The value task is already complete, and for a failure it is faulted.
    /// </summary>
    CompletedValueTask,

    /// <summary>
    /// The operation is an <see langword="async"/> method that throws or returns before its first
    /// <see langword="await"/>. The value task is already complete, and for a failure it is faulted.
    /// </summary>
    AsyncBeforeAwait,

    /// <summary>
    /// The operation is an <see langword="async"/> method that throws or returns after an <see langword="await"/> that
    /// yields. The value task is not complete when the operation returns.
    /// </summary>
    AsyncAfterAwait,
}
