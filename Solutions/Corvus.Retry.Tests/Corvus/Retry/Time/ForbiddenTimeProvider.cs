// <copyright file="ForbiddenTimeProvider.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Time;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The default <see cref="RetryDefaults.TimeProvider"/> of the test assembly. It stops a test from waiting for real
/// time: a timer with a finite due time that is not zero fails the test.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AssemblySetup"/> installs <see cref="Instance"/> before the first test, and
/// <see cref="TimeProviderFixture.Reset"/> puts it back after each test that installs a fake provider. A test that
/// uses a delay through <see cref="Retriable"/>, <see cref="RetryTaskFactory"/> or <see cref="ReliableTaskRunner"/>
/// must install a fake provider with <see cref="TimeProviderFixture.Install{T}(T)"/>.
/// </para>
/// <para>
/// A zero due time and <see cref="Timeout.InfiniteTimeSpan"/> do not wait, so they are allowed. Each forbidden timer
/// throws an <see cref="AssertFailedException"/> to the code that asked for it, and is also recorded, so that
/// <see cref="AssemblySetup"/> can fail the run if code caught that exception.
/// </para>
/// </remarks>
internal sealed class ForbiddenTimeProvider : TimeProvider
{
    private readonly ConcurrentQueue<string> violations = new();

    /// <summary>
    /// Gets the instance that the test assembly installs.
    /// </summary>
    public static ForbiddenTimeProvider Instance { get; } = new();

    /// <summary>
    /// Gets a description of each forbidden timer so far, with its stack trace.
    /// </summary>
    public IReadOnlyCollection<string> Violations => this.violations.ToArray();

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        this.Check(dueTime);
        return new GuardedTimer(this, base.CreateTimer(callback, state, dueTime, period));
    }

    private void Check(TimeSpan dueTime)
    {
        if (dueTime == TimeSpan.Zero || dueTime == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        string message =
            $"A test asked RetryDefaults.TimeProvider for a timer of {dueTime}, which would wait for real time. Install a fake "
            + "time provider with TimeProviderFixture.Install, in a [DoNotParallelize] class that calls TimeProviderFixture.Reset "
            + "in [TestCleanup].";
        this.violations.Enqueue($"{message}{Environment.NewLine}{Environment.StackTrace}");
        throw new AssertFailedException(message);
    }

    private sealed class GuardedTimer(ForbiddenTimeProvider owner, ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Check(dueTime);
            return inner.Change(dueTime, period);
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
