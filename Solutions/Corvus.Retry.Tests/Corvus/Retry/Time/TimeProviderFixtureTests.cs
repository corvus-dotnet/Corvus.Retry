// <copyright file="TimeProviderFixtureTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Time;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The rules that keep the process-wide <see cref="RetryDefaults.TimeProvider"/> safe: no test waits for real time,
/// and a test class that installs a fake provider does not run in parallel with other tests and restores the guard.
/// </summary>
/// <remarks>
/// The rules are checked in the compiled test assembly. A call in a lambda, a local function or an async method is in a
/// compiler-generated type nested in the test class, so the check reads the IL of every nested type too.
/// </remarks>
[TestClass]
public class TimeProviderFixtureTests
{
    private static readonly MethodInfo InstallMethod = typeof(TimeProviderFixture).GetMethod(nameof(TimeProviderFixture.Install))!;
    private static readonly MethodInfo ResetMethod = typeof(TimeProviderFixture).GetMethod(nameof(TimeProviderFixture.Reset))!;
    private static readonly MethodInfo SetTimeProviderMethod = typeof(RetryDefaults).GetProperty(nameof(RetryDefaults.TimeProvider), BindingFlags.NonPublic | BindingFlags.Static)!.SetMethod!;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [TestMethod]
    public void EveryTestClassThatInstallsATimeProviderDoesNotRunInParallelAndResetsTheProviderInItsCleanup()
    {
        List<Type> installers = TestClasses().Where(t => Calls(t, InstallMethod)).ToList();

        // The check is not empty: these classes install a provider today.
        CollectionAssert.IsSubsetOf(new[] { typeof(RetriableDelayTests) }, installers);

        foreach (Type type in installers)
        {
            Assert.IsNotNull(type.GetCustomAttribute<DoNotParallelizeAttribute>(), $"{type.Name} calls TimeProviderFixture.Install, so it must be [DoNotParallelize].");

            MethodInfo[] cleanups = [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.GetCustomAttribute<TestCleanupAttribute>() is not null)];
            Assert.Contains(
                m => CalledMethods(m).Any(c => IsMethod(c, ResetMethod)),
                cleanups,
                $"{type.Name} calls TimeProviderFixture.Install, so its [TestCleanup] must call TimeProviderFixture.Reset.");
        }
    }

    [TestMethod]
    public void OnlyTheFixtureSetsTheProcessWideTimeProvider()
    {
        List<Type> setters = typeof(TimeProviderFixtureTests).Assembly.GetTypes()
            .Where(t => !t.IsNested && t != typeof(TimeProviderFixture) && Calls(t, SetTimeProviderMethod))
            .ToList();

        Assert.IsEmpty(setters, $"Use TimeProviderFixture instead of setting RetryDefaults.TimeProvider in: {string.Join(", ", setters.Select(t => t.Name))}");
    }

    [TestMethod]
    public void TheForbiddenTimeProviderAllowsATimerThatDoesNotWaitAndRejectsOneThatWaits()
    {
        // A separate instance, so that the violations of this test do not fail the run.
        var provider = new ForbiddenTimeProvider();

        using (ITimer zero = provider.CreateTimer(_ => { }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan))
        using (ITimer infinite = provider.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
        {
            Assert.IsEmpty(provider.Violations);

            AssertFailedException changed = Assert.ThrowsExactly<AssertFailedException>(() => infinite.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan));
            Assert.Contains("TimeProviderFixture.Install", changed.Message);
        }

        AssertFailedException created = Assert.ThrowsExactly<AssertFailedException>(() => provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan));
        Assert.Contains("00:00:00.1000000", created.Message);
        Assert.HasCount(2, provider.Violations);
    }

    private static IEnumerable<Type> TestClasses()
    {
        return typeof(TimeProviderFixtureTests).Assembly.GetTypes().Where(t => t.GetCustomAttribute<TestClassAttribute>() is not null);
    }

    private static bool Calls(Type type, MethodInfo target)
    {
        return AllTypes(type)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .SelectMany(CalledMethods)
            .Any(c => IsMethod(c, target));
    }

    private static IEnumerable<Type> AllTypes(Type type)
    {
        yield return type;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(AllTypes))
        {
            yield return nested;
        }
    }

    private static bool IsMethod(MethodBase candidate, MethodInfo target)
    {
        return candidate is MethodInfo method && (method.IsGenericMethod ? method.GetGenericMethodDefinition() : method) == target;
    }

    // The methods that a method calls, including those that its async state machine calls.
    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        IEnumerable<MethodBase> called = ReadCalls(method);
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncStateMachine)
        {
            called = called.Concat(ReadCalls(asyncStateMachine.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!));
        }

        return called;
    }

    private static List<MethodBase> ReadCalls(MethodBase method)
    {
        var called = new List<MethodBase>();
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return called;
        }

        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } declaringType ? declaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int position = 0;
        while (position < il.Length)
        {
            OpCode opCode = il[position] == 0xFE
                ? OpCodesByValue[unchecked((short)(0xFE00 | il[position + 1]))]
                : OpCodesByValue[il[position]];
            position += opCode.Size;

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                called.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!);
            }

            position += OperandSize(opCode.OperandType, il, position);
        }

        return called;
    }

    private static int OperandSize(OperandType operandType, byte[] il, int position)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
            _ => 4,
        };
    }
}
