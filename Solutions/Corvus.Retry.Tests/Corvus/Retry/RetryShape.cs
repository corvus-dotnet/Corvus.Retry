// <copyright file="RetryShape.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    /// <summary>
    /// The four shapes of operation that <see cref="Retriable"/> can retry. Each has its own retry loop in the library.
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
    }
}
