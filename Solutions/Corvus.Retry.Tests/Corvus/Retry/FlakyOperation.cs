// <copyright file="FlakyOperation.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// An operation that fails a configurable number of times before succeeding and records every invocation.
/// </summary>
internal sealed class FlakyOperation
{
    public const int AlwaysFails = int.MaxValue;

    private readonly int failuresBeforeSuccess;
    private readonly Func<int, Exception> exceptionFactory;
    private readonly object sync = new();
    private readonly List<Exception> thrown = new();
    private int invocations;

    public FlakyOperation(int failuresBeforeSuccess, Func<int, Exception>? exceptionFactory = null)
    {
        this.failuresBeforeSuccess = failuresBeforeSuccess;
        this.exceptionFactory = exceptionFactory ?? (attempt => new InvalidOperationException($"Attempt {attempt} failed"));
    }

    public int Invocations => Volatile.Read(ref this.invocations);

    /// <summary>
    /// Gets every exception thrown so far, in order. Each attempt throws a distinct instance.
    /// </summary>
    public IReadOnlyList<Exception> Thrown
    {
        get
        {
            lock (this.sync)
            {
                return this.thrown.ToArray();
            }
        }
    }

    public Exception LastThrown => this.Thrown[^1];

    /// <summary>
    /// Runs one attempt. Returns "attempt-N" on success so tests can prove which attempt produced the result.
    /// </summary>
    /// <returns>The name of the successful attempt.</returns>
    public string Invoke()
    {
        int attempt = Interlocked.Increment(ref this.invocations);
        if (attempt <= this.failuresBeforeSuccess)
        {
            Exception exception = this.exceptionFactory(attempt);
            lock (this.sync)
            {
                this.thrown.Add(exception);
            }

            throw exception;
        }

        return $"attempt-{attempt}";
    }

    public Task InvokeAsync()
    {
        this.Invoke();
        return Task.CompletedTask;
    }

    public Task<string> InvokeAsyncReturning()
    {
        return Task.FromResult(this.Invoke());
    }
}
