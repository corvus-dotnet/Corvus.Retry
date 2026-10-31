// <copyright file="RetryTaskFactoryArgumentValidationTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Every public <c>StartNew</c> overload rejects a null operation, scheduler, strategy or policy immediately, rather
/// than starting a task that fails later.
/// </summary>
/// <remarks>
/// <see cref="RetryTaskFactory{T}"/> for a task type also throws <see cref="NotSupportedException"/>. A null argument
/// is reported first, so the same cases run for <c>RetryTaskFactory&lt;Task&gt;</c>.
/// </remarks>
[TestClass]
public class RetryTaskFactoryArgumentValidationTests
{
    private static readonly Dictionary<string, Type> FactoryTypes = new()
    {
        ["RetryTaskFactory"] = typeof(RetryTaskFactory),
        ["RetryTaskFactory<int>"] = typeof(RetryTaskFactory<int>),
        ["RetryTaskFactory<Task>"] = typeof(RetryTaskFactory<Task>),
    };

    /// <summary>
    /// Gets one case per (factory, overload, nullable reference parameter).
    /// </summary>
    public static IEnumerable<object[]> NullableArguments =>
        from factory in FactoryTypes
        from method in StartNewMethods(factory.Value)
        from parameter in method.GetParameters()
        where IsValidatedReference(parameter.ParameterType)
        select new object[] { factory.Key, method.ToString()!, parameter.Name! };

    [TestMethod]
    [DynamicData(nameof(NullableArguments))]
    public void WhenARequiredArgumentIsNull_StartNewThrowsArgumentNullException(string factoryName, string signature, string parameterName)
    {
        Type factoryType = FactoryTypes[factoryName];
        MethodInfo method = StartNewMethods(factoryType).Single(m => m.ToString() == signature);
        object factory = Activator.CreateInstance(factoryType)!;
        object?[] arguments = method.GetParameters()
            .Select(p => p.Name == parameterName ? null : ValidArgument(p.ParameterType))
            .ToArray();

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => Invoke(method, factory, arguments));

        Assert.AreEqual(parameterName, thrown.ParamName);
    }

    private static IEnumerable<MethodInfo> StartNewMethods(Type factoryType)
    {
        // The obsolete overloads only stop an asynchronous action from binding to StartNew. No call can reach them.
        return factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "StartNew" && m.GetCustomAttribute<ObsoleteAttribute>() is null);
    }

    private static bool IsValidatedReference(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || type == typeof(TaskScheduler) || type == typeof(IRetryStrategy) || type == typeof(IRetryPolicy);
    }

    private static object? ValidArgument(Type type)
    {
        return type switch
        {
            _ when type == typeof(Action) => new Action(() => { }),
            _ when type == typeof(Action<object?>) => new Action<object?>(_ => { }),
            _ when type == typeof(Func<int>) => new Func<int>(() => 0),
            _ when type == typeof(Func<object?, int>) => new Func<object?, int>(_ => 0),
            _ when type == typeof(Func<Task>) => new Func<Task>(() => Task.CompletedTask),
            _ when type == typeof(Func<object?, Task>) => new Func<object?, Task>(_ => Task.CompletedTask),
            _ when type == typeof(object) => null,
            _ when type == typeof(CancellationToken) => CancellationToken.None,
            _ when type == typeof(TaskCreationOptions) => TaskCreationOptions.None,
            _ when type == typeof(TaskScheduler) => TaskScheduler.Default,
            _ when type == typeof(IRetryStrategy) => new Count(),
            _ when type == typeof(IRetryPolicy) => new AnyExceptionPolicy(),
            _ => throw new NotSupportedException($"No valid value is defined for a StartNew parameter of type {type}."),
        };
    }

    private static void Invoke(MethodInfo method, object factory, object?[] arguments)
    {
        try
        {
            method.Invoke(factory, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }
}
