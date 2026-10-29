// <copyright file="RetryTaskTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

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
        const int Threads = 16;
        var factories = new RetryTaskFactory<FirstAccessMarker>[Threads];
        using Barrier start = new(Threads);

        Parallel.For(
            0,
            Threads,
            new ParallelOptions { MaxDegreeOfParallelism = Threads },
            i =>
            {
                start.SignalAndWait();
                factories[i] = RetryTask<FirstAccessMarker>.Factory;
            });

        Assert.IsTrue(factories.All(f => ReferenceEquals(f, factories[0])));
    }

    private sealed class FirstAccessMarker
    {
    }
}
