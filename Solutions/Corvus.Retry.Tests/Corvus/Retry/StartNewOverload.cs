// <copyright file="StartNewOverload.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    /// <summary>
    /// The task-related parameters a <c>StartNew</c> overload takes, mirroring <see cref="System.Threading.Tasks.TaskFactory.StartNew(System.Action)"/>.
    /// </summary>
    public enum StartNewOverload
    {
        /// <summary><c>StartNew(action)</c>.</summary>
        Plain,

        /// <summary><c>StartNew(action, cancellationToken)</c>.</summary>
        WithCancellationToken,

        /// <summary><c>StartNew(action, taskCreationOptions)</c>.</summary>
        WithCreationOptions,

        /// <summary><c>StartNew(action, state)</c>.</summary>
        WithState,

        /// <summary><c>StartNew(action, state, taskCreationOptions)</c>.</summary>
        WithStateAndCreationOptions,

        /// <summary><c>StartNew(action, state, cancellationToken)</c>. Only exists with both a strategy and a policy.</summary>
        WithStateAndCancellationToken,

        /// <summary><c>StartNew(action, state, cancellationToken, taskCreationOptions, scheduler)</c>.</summary>
        WithStateCancellationTokenOptionsAndScheduler,

        /// <summary><c>StartNew(action, cancellationToken, taskCreationOptions, scheduler)</c>.</summary>
        WithCancellationTokenOptionsAndScheduler,
    }
}
