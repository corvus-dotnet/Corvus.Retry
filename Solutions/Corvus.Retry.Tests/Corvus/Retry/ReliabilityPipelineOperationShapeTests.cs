// <copyright file="ReliabilityPipelineOperationShapeTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Each <c>ExecuteAsync</c> method of <see cref="ReliabilityPipeline"/> and <see cref="ReliabilityPipeline{T}"/> gives the
/// same outcome for every <see cref="OperationShape"/>: a failure is retried and the last exception is rethrown, whether
/// the operation throws synchronously, returns a faulted value task, or faults asynchronously; a success is returned,
/// whether it is synchronous or asynchronous; and a rejected result is disposed.
/// </summary>
[TestClass]
public class ReliabilityPipelineOperationShapeTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationWithNoResultFailsEveryTime_ItIsRetriedAndTheLastExceptionIsThrown(OperationShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(PipelineOperationDriver.WithoutResult(shape, () => op.Invoke())).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationWithNoResultFailsTwice_TheThirdAttemptSucceeds(OperationShape shape)
    {
        var op = new FlakyOperation(2);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        await pipeline.ExecuteAsync(PipelineOperationDriver.WithoutResult(shape, () => op.Invoke())).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationWithAResultFailsEveryTime_ItIsRetriedAndTheLastExceptionIsThrown(OperationShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, op.Invoke)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationWithAResultFailsTwice_TheResultOfTheThirdAttemptIsReturned(OperationShape shape)
    {
        var op = new FlakyOperation(2);
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new Count(3) });

        string result = await pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, op.Invoke)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-3", result);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationOfTheGenericPipelineFailsEveryTime_ItIsRetriedAndTheLastExceptionIsThrown(OperationShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string> { Strategy = new Count(3) });

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, op.Invoke)).AsTask().WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(3, op.Invocations);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenTheOperationOfTheGenericPipelineFailsEveryTime_TheFallbackReceivesTheLastException(OperationShape shape)
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);
        var received = new List<Exception?>();
        var pipeline = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
        {
            Strategy = new Count(3),
            Fallback = new FallbackOptions<string>
            {
                Action = args =>
                {
                    lock (received)
                    {
                        received.Add(args.Exception);
                    }

                    return new ValueTask<string>("fallback");
                },
            },
        });

        string result = await pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, op.Invoke)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("fallback", result);
        Assert.AreEqual(3, op.Invocations);
        Assert.AreSame(op.LastThrown, Assert.ContainsSingle(received));
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task TheGenericPipelineDisposesEachRejectedResultAndReturnsTheResultItAccepts(OperationShape shape)
    {
        var results = new List<Result>();
        var pipeline = new ReliabilityPipeline<Result>(new ReliabilityPipelineOptions<Result>
        {
            Strategy = new Count(3),
            ShouldRetryResult = r => !r.IsGood,
        });

        Result Next()
        {
            lock (results)
            {
                var result = new Result(isGood: results.Count == 2);
                results.Add(result);
                return result;
            }
        }

        Result returned = await pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, Next)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.HasCount(3, results);
        Assert.AreSame(results[2], returned);
        Assert.IsFalse(returned.Disposed);
        Assert.IsTrue(results[0].Disposed);
        Assert.IsTrue(results[1].Disposed);
    }

    [TestMethod]
    [DataRow(OperationShape.Synchronous)]
    [DataRow(OperationShape.CompletedValueTask)]
    [DataRow(OperationShape.AsyncBeforeAwait)]
    [DataRow(OperationShape.AsyncAfterAwait)]
    public async Task WhenNoRetriesRemainForARejectedResult_TheGenericPipelineReturnsTheLastResultAndDisposesTheOthers(OperationShape shape)
    {
        var results = new List<Result>();
        var pipeline = new ReliabilityPipeline<Result>(new ReliabilityPipelineOptions<Result>
        {
            Strategy = new Count(3),
            ShouldRetryResult = r => !r.IsGood,
        });

        Result Next()
        {
            lock (results)
            {
                var result = new Result(isGood: false);
                results.Add(result);
                return result;
            }
        }

        Result returned = await pipeline.ExecuteAsync(PipelineOperationDriver.WithResult(shape, Next)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.HasCount(3, results);
        Assert.AreSame(results[2], returned);
        Assert.IsFalse(returned.Disposed);
        Assert.IsTrue(results[0].Disposed);
        Assert.IsTrue(results[1].Disposed);
    }

    // A disposable result that records its disposal.
    private sealed class Result(bool isGood) : IDisposable
    {
        private int disposed;

        public bool IsGood => isGood;

        public bool Disposed => Volatile.Read(ref this.disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref this.disposed, 1);
    }
}
