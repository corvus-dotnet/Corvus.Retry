// <copyright file="RetryShape.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The shapes of operation that <see cref="Retriable"/> can retry, each through its own overload.
/// </summary>
public enum RetryShape
{
    /// <summary>A synchronous <c>Action</c>.</summary>
    Action,

    /// <summary>A synchronous <c>Func&lt;T&gt;</c>.</summary>
    Function,

    /// <summary>An asynchronous <c>Func&lt;Task&gt;</c>.</summary>
    AsyncAction,

    /// <summary>An asynchronous <c>Func&lt;Task&lt;T&gt;&gt;</c>.</summary>
    AsyncFunction,

    /// <summary>An asynchronous <c>Func&lt;CancellationToken, Task&gt;</c>.</summary>
    AsyncActionWithToken,

    /// <summary>An asynchronous <c>Func&lt;CancellationToken, Task&lt;T&gt;&gt;</c>.</summary>
    AsyncFunctionWithToken,
}
