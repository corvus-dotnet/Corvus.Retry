// <copyright file="RetryTaskFactoryKind.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// The factory under test, in tests that assert the same behaviour of <see cref="RetryTaskFactory"/> and
/// <see cref="RetryTaskFactory{T}"/>.
/// </summary>
public enum RetryTaskFactoryKind
{
    /// <summary><see cref="Retry.RetryTaskFactory"/>.</summary>
    RetryTaskFactory,

    /// <summary><see cref="RetryTaskFactory{T}"/>, with a <see cref="string"/> result.</summary>
    RetryTaskFactoryOfT,
}
