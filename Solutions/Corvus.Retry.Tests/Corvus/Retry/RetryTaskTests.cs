// <copyright file="RetryTaskTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="RetryTask"/> and <see cref="RetryTask{T}"/> expose a shared factory, like <see cref="System.Threading.Tasks.Task.Factory"/>,
/// and their <c>Run</c> methods use it. <see cref="HedgedTask"/> and <see cref="HedgedTask{T}"/> do the same for hedging.
/// </summary>
[TestClass]
public class RetryTaskTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void FactoryReturnsTheSameInstanceEveryTime()
    {
        RetryTaskFactory first = RetryTask.Factory;
        RetryTaskFactory second = RetryTask.Factory;

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void TheGenericFactoryReturnsTheSameInstanceEveryTime()
    {
        RetryTaskFactory<int> first = RetryTask<int>.Factory;
        RetryTaskFactory<int> second = RetryTask<int>.Factory;

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void WhenManyThreadsGetTheFactoryForTheFirstTime_TheyAllGetTheSameInstance()
    {
        // No other test uses this type argument, so this is the first access to this RetryTask<T>.Factory.
        RetryTaskFactory<FirstAccessMarker>[] factories = GetOnManyThreadsAtOnce(() => RetryTask<FirstAccessMarker>.Factory);

        Assert.IsNotNull(factories[0]);
        Assert.IsTrue(factories.All(f => ReferenceEquals(f, factories[0])));
    }

    [TestMethod]
    public void WhenManyThreadsGetTheNonGenericFactoryAtOnce_TheyAllGetTheSameInstance()
    {
        // Other tests may have read RetryTask.Factory already, so this cannot be sure to be the first access. The static
        // initialiser makes the first access safe; this checks that every thread sees one instance.
        RetryTaskFactory[] factories = GetOnManyThreadsAtOnce(() => RetryTask.Factory);

        Assert.IsNotNull(factories[0]);
        Assert.IsTrue(factories.All(f => ReferenceEquals(f, factories[0])));
    }

    [TestMethod]
    public async Task RetryTaskRunMakesFiveTriesWithTheDefaultFactoryAndRethrowsTheLastException()
    {
        // The default factory has no delay, so these calls create no timer.
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RetryTask.Run(_ => op.InvokeAsync()).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(5, op.Invocations);
    }

    [TestMethod]
    public async Task RetryTaskRunReturnsTheResultOfTheFirstAttemptThatSucceeds()
    {
        var op = new FlakyOperation(4);
        var opOfT = new FlakyOperation(4);

        string result = await RetryTask.Run(_ => op.InvokeAsyncReturning()).WaitAsync(WaitLimit).ConfigureAwait(false);
        string resultOfT = await RetryTask<string>.Run(_ => opOfT.InvokeAsyncReturning()).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual("attempt-5", result);
        Assert.AreEqual("attempt-5", resultOfT);
    }

    [TestMethod]
    public async Task RetryTaskRunPassesTheCallersTokenToTheOperation()
    {
        using CancellationTokenSource cts = new();
        CancellationToken received = default;

        await RetryTask.Run(
            ct =>
            {
                received = ct;
                return Task.CompletedTask;
            },
            cts.Token).WaitAsync(WaitLimit).ConfigureAwait(false);

        // The default factory has no timeouts, so the operation receives the caller's token itself.
        Assert.AreEqual(cts.Token, received);
    }

    [TestMethod]
    public void RetryTaskRunOfTheGenericTypeThrowsForATaskResult()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => RetryTask<Task<int>>.Run(_ => Task.FromResult(Task.FromResult(1))));
    }

    [TestMethod]
    public void EachRunRejectsANullOperation()
    {
        ArgumentNullException run = Assert.ThrowsExactly<ArgumentNullException>(() => RetryTask.Run((Func<CancellationToken, Task>)null!));
        ArgumentNullException runOfT = Assert.ThrowsExactly<ArgumentNullException>(() => RetryTask.Run((Func<CancellationToken, Task<int>>)null!));
        ArgumentNullException genericRun = Assert.ThrowsExactly<ArgumentNullException>(() => RetryTask<int>.Run(null!));
        ArgumentNullException hedgedRun = Assert.ThrowsExactly<ArgumentNullException>(() => HedgedTask.Run((Func<CancellationToken, Task>)null!));
        ArgumentNullException hedgedRunOfT = Assert.ThrowsExactly<ArgumentNullException>(() => HedgedTask.Run((Func<CancellationToken, Task<int>>)null!));
        ArgumentNullException genericHedgedRun = Assert.ThrowsExactly<ArgumentNullException>(() => HedgedTask<int>.Run(null!));

        foreach (ArgumentNullException thrown in new[] { run, runOfT, genericRun, hedgedRun, hedgedRunOfT, genericHedgedRun })
        {
            Assert.AreEqual("operation", thrown.ParamName);
        }
    }

    [TestMethod]
    public async Task HedgedTaskRunStartsTheExtraAttemptAtOnceWhenTheFirstFails()
    {
        // The first attempt fails synchronously, so the extra attempt starts at once, and no stagger timer starts.
        var op = new FlakyOperation(1);
        var opOfT = new FlakyOperation(1);
        var opOfGeneric = new FlakyOperation(1);

        await HedgedTask.Run(_ => op.InvokeAsync()).WaitAsync(WaitLimit).ConfigureAwait(false);
        string result = await HedgedTask.Run(_ => opOfT.InvokeAsyncReturning()).WaitAsync(WaitLimit).ConfigureAwait(false);
        string resultOfGeneric = await HedgedTask<string>.Run(_ => opOfGeneric.InvokeAsyncReturning()).WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(2, op.Invocations);
        Assert.AreEqual("attempt-2", result);
        Assert.AreEqual("attempt-2", resultOfGeneric);
    }

    [TestMethod]
    public async Task HedgedTaskRunMakesOneExtraAttemptAndRethrowsTheLastException()
    {
        var op = new FlakyOperation(FlakyOperation.AlwaysFails);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => HedgedTask.Run(_ => op.InvokeAsync()).WaitAsync(WaitLimit)).ConfigureAwait(false);

        Assert.AreSame(op.LastThrown, thrown);
        Assert.AreEqual(2, op.Invocations);
    }

    private static T[] GetOnManyThreadsAtOnce<T>(Func<T> get)
    {
        // Dedicated threads, not the thread pool: threads that wait at the barrier would hold pool threads that other
        // tests need.
        const int Threads = 16;
        var results = new T[Threads];
        using Barrier start = new(Threads);

        Thread[] threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            results[i] = get();
        })).ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return results;
    }

    private sealed class FirstAccessMarker
    {
    }
}
