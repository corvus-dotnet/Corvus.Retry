// <copyright file="PooledValueTaskSourceTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Corvus.Retry.Strategies;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Each <c>ExecuteAsync</c> method of the pipelines consumes the value task of each attempt exactly once, so an operation
/// may return a value task that is backed by a pooled <see cref="IValueTaskSource{TResult}"/> or
/// <see cref="IValueTaskSource"/>: the pipeline gets the result of each attempt one time, never uses the token after
/// that, and invokes the operation again for a retry, which gets a fresh value task from the same pooled source.
/// </summary>
/// <remarks>
/// These tests pin the current behaviour. A value task backed by a source must be consumed exactly once; a second
/// <c>GetResult</c>, or any call with a token after the source has been reset, is undefined for a real pooled source
/// (it may return another caller's result). The test double detects both.
/// </remarks>
[TestClass]
public class PooledValueTaskSourceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    public enum Method
    {
        /// <summary><see cref="ReliabilityPipeline.ExecuteAsync(Func{CancellationToken, ValueTask}, CancellationToken)"/>.</summary>
        PipelineWithoutResult,

        /// <summary><see cref="ReliabilityPipeline.ExecuteAsync{T}(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/>.</summary>
        PipelineWithResult,

        /// <summary><see cref="ReliabilityPipeline{T}.ExecuteAsync(Func{CancellationToken, ValueTask{T}}, CancellationToken)"/>.</summary>
        GenericPipeline,
    }

    public enum Scenario
    {
        /// <summary>The value task is complete with a result when the operation returns it.</summary>
        SynchronousSuccess,

        /// <summary>The value task completes with a result on another thread, after the pipeline awaits it.</summary>
        AsynchronousSuccess,

        /// <summary>Every attempt's value task is faulted when the operation returns it.</summary>
        SynchronousFailure,

        /// <summary>Every attempt's value task faults on another thread, after the pipeline awaits it.</summary>
        AsynchronousFailure,

        /// <summary>The first attempt's value task is faulted when returned; the retry completes asynchronously.</summary>
        SynchronousFailureThenAsynchronousSuccess,

        /// <summary>The first attempt's value task faults asynchronously; the retry is complete when returned.</summary>
        AsynchronousFailureThenSynchronousSuccess,
    }

    public static IEnumerable<object[]> Rows =>
        from method in Enum.GetValues<Method>()
        from scenario in Enum.GetValues<Scenario>()
        from withAttemptTimeout in new[] { false, true }
        select new object[] { method, scenario, withAttemptTimeout };

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public async Task EachAttemptsValueTaskIsConsumedExactlyOnce_AndARetryGetsAFreshValueTask(Method method, Scenario scenario, bool withAttemptTimeout)
    {
        Attempt[] attempts = Attempts(scenario);
        var source = new PooledSource();
        int invocations = 0;
        Attempt NextAttempt() => attempts[Interlocked.Increment(ref invocations) - 1];

        // The fake time provider never advances, so an attempt timeout never expires; it only changes how the engine
        // runs each attempt (it creates a linked token for the attempt).
        var options = new ReliabilityPipelineOptions<Payload>
        {
            Strategy = new Count(attempts.Length),
            AttemptTimeout = withAttemptTimeout ? TimeSpan.FromHours(1) : null,
            TimeProvider = new FakeTimeProvider(),
        };

        Task<Payload?> call = method switch
        {
            Method.PipelineWithoutResult => WithoutResultAsync(new ReliabilityPipeline(options).ExecuteAsync(_ => source.NextWithoutResult(NextAttempt()))),
            Method.PipelineWithResult => WithResultAsync(new ReliabilityPipeline(options).ExecuteAsync(_ => source.Next(NextAttempt()))),
            Method.GenericPipeline => WithResultAsync(new ReliabilityPipeline<Payload>(options).ExecuteAsync(_ => source.Next(NextAttempt()))),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        Attempt last = attempts[^1];
        if (last.Exception is Exception expected)
        {
            InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(WaitLimit)).ConfigureAwait(false);
            Assert.AreSame(expected, thrown);
        }
        else
        {
            Payload? result = await call.WaitAsync(WaitLimit).ConfigureAwait(false);
            if (method == Method.PipelineWithoutResult)
            {
                Assert.IsNull(result);
            }
            else
            {
                Assert.AreSame(last.Result, result);
            }
        }

        // The operation was invoked once for each attempt, and each invocation got a fresh value task: the source was
        // reset between them, so each has its own token.
        Assert.AreEqual(attempts.Length, Volatile.Read(ref invocations));
        IReadOnlyList<short> issued = source.Issued;
        Assert.HasCount(attempts.Length, issued);
        Assert.HasCount(attempts.Length, issued.Distinct());

        // Exactly one GetResult for each attempt, with that attempt's token, in order.
        Assert.AreSequenceEqual(issued, source.Calls.Where(c => c.Call == nameof(PooledSource.GetResult)).Select(c => c.Token));

        // The pipeline awaited (registered a continuation) only an attempt that was not complete when it was returned.
        for (int i = 0; i < attempts.Length; i++)
        {
            int onCompleted = source.Calls.Count(c => c.Call == nameof(PooledSource.OnCompleted) && c.Token == issued[i]);
            Assert.AreEqual(attempts[i].Asynchronous ? 1 : 0, onCompleted, $"OnCompleted calls for attempt {i + 1}");
        }

        Assert.IsEmpty(source.Violations, string.Join(Environment.NewLine, source.Violations));
    }

    private static Attempt[] Attempts(Scenario scenario)
    {
        return scenario switch
        {
            Scenario.SynchronousSuccess => [Attempt.Succeeds(asynchronous: false)],
            Scenario.AsynchronousSuccess => [Attempt.Succeeds(asynchronous: true)],
            Scenario.SynchronousFailure => [Attempt.Fails(asynchronous: false, 1), Attempt.Fails(asynchronous: false, 2)],
            Scenario.AsynchronousFailure => [Attempt.Fails(asynchronous: true, 1), Attempt.Fails(asynchronous: true, 2)],
            Scenario.SynchronousFailureThenAsynchronousSuccess => [Attempt.Fails(asynchronous: false, 1), Attempt.Succeeds(asynchronous: true)],
            Scenario.AsynchronousFailureThenSynchronousSuccess => [Attempt.Fails(asynchronous: true, 1), Attempt.Succeeds(asynchronous: false)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private static async Task<Payload?> WithoutResultAsync(ValueTask call)
    {
        await call.ConfigureAwait(false);
        return null;
    }

    private static async Task<Payload?> WithResultAsync(ValueTask<Payload> call)
    {
        return await call.ConfigureAwait(false);
    }

    // A result whose identity the test checks.
    private sealed class Payload;

    // The outcome of one attempt, and whether its value task completes after the pipeline awaits it.
    private sealed record Attempt(bool Asynchronous, InvalidOperationException? Exception, Payload? Result)
    {
        public static Attempt Succeeds(bool asynchronous) => new(asynchronous, null, new Payload());

        public static Attempt Fails(bool asynchronous, int number) => new(asynchronous, new InvalidOperationException($"Attempt {number} failed"), null);
    }

    // A value task source that is reused, as a pooled source would be: getting the result resets it, so that its token
    // is stale. It records every call, and throws InvalidOperationException (and records a violation) for a second
    // GetResult or for any call with a stale token. An attempt that is asynchronous completes on the thread pool after
    // the continuation is registered.
    private sealed class PooledSource : IValueTaskSource<Payload>, IValueTaskSource
    {
        private readonly Lock sync = new();
        private readonly List<(string Call, short Token)> calls = [];
        private readonly List<short> issued = [];
        private readonly List<string> violations = [];
        private ManualResetValueTaskSourceCore<Payload> core;
        private bool outstanding;
        private Attempt? pending;

        public IReadOnlyList<(string Call, short Token)> Calls
        {
            get
            {
                lock (this.sync)
                {
                    return this.calls.ToArray();
                }
            }
        }

        public IReadOnlyList<short> Issued
        {
            get
            {
                lock (this.sync)
                {
                    return this.issued.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Violations
        {
            get
            {
                lock (this.sync)
                {
                    return this.violations.ToArray();
                }
            }
        }

        public ValueTask<Payload> Next(Attempt attempt) => new(this, this.Arm(attempt));

        public ValueTask NextWithoutResult(Attempt attempt) => new(this, this.Arm(attempt));

        public ValueTaskSourceStatus GetStatus(short token)
        {
            lock (this.sync)
            {
                this.calls.Add((nameof(this.GetStatus), token));
                this.ThrowIfStale(nameof(this.GetStatus), token);
                return this.core.GetStatus(token);
            }
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Attempt? toComplete;
            lock (this.sync)
            {
                this.calls.Add((nameof(this.OnCompleted), token));
                this.ThrowIfStale(nameof(this.OnCompleted), token);
                this.core.OnCompleted(continuation, state, token, flags);
                toComplete = this.pending;
                this.pending = null;
            }

            // Complete only now that the pipeline has awaited, and on another thread.
            if (toComplete is not null)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.Source.Complete(s.Attempt), (Source: this, Attempt: toComplete), preferLocal: false);
            }
        }

        public Payload GetResult(short token)
        {
            lock (this.sync)
            {
                this.calls.Add((nameof(this.GetResult), token));
                this.ThrowIfStale(nameof(this.GetResult), token);
                this.outstanding = false;
                try
                {
                    return this.core.GetResult(token);
                }
                finally
                {
                    // Return the source to the pool. The token of the value task that was just consumed is now stale.
                    this.core.Reset();
                }
            }
        }

        void IValueTaskSource.GetResult(short token) => this.GetResult(token);

        private short Arm(Attempt attempt)
        {
            lock (this.sync)
            {
                if (this.outstanding)
                {
                    this.violations.Add($"A new value task was requested before the result of token {this.core.Version} was taken.");
                    throw new InvalidOperationException("The pooled source is still in use.");
                }

                this.outstanding = true;
                short token = this.core.Version;
                this.issued.Add(token);
                if (attempt.Asynchronous)
                {
                    this.pending = attempt;
                }
                else
                {
                    this.Complete(attempt);
                }

                return token;
            }
        }

        private void Complete(Attempt attempt)
        {
            if (attempt.Exception is not null)
            {
                this.core.SetException(attempt.Exception);
            }
            else
            {
                this.core.SetResult(attempt.Result!);
            }
        }

        // Call this method under the lock.
        private void ThrowIfStale(string call, short token)
        {
            if (!this.outstanding || token != this.core.Version)
            {
                this.violations.Add($"{call} was called with token {token}, but the source is at token {this.core.Version} and {(this.outstanding ? "in use" : "free")}.");
                throw new InvalidOperationException($"{call} was called with a stale token.");
            }
        }
    }
}
