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
/// <see cref="RetryTask"/> and <see cref="RetryTask{T}"/> expose a shared factory, like <see cref="System.Threading.Tasks.Task.Factory"/>.
/// </summary>
[TestClass]
public class RetryTaskTests
{
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
