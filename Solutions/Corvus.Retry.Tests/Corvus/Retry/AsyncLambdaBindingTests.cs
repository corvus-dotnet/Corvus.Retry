// <copyright file="AsyncLambdaBindingTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// An asynchronous lambda passed to <see cref="Retriable.Retry{T}(Func{T})"/> or to
/// <see cref="RetryTaskFactory.StartNew(Action)"/> is a compile error, and synchronous lambdas bind as before.
/// An operation that returns a task inside its task, passed to <c>RetryAsync</c>, to
/// <see cref="RetryTaskFactory.Run{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{T}}, System.Threading.CancellationToken)"/>,
/// <see cref="RetryTaskFactory.RunValueTask{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.ValueTask{T}}, System.Threading.CancellationToken)"/>,
/// <see cref="RetryTask.Run{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{T}}, System.Threading.CancellationToken)"/>,
/// <see cref="HedgedTaskFactory.Run{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{T}}, System.Threading.CancellationToken)"/>
/// or <see cref="HedgedTask.Run{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{T}}, System.Threading.CancellationToken)"/>,
/// is also a compile error.
/// </summary>
/// <remarks>
/// <para>
/// Without the trap overloads, <c>Retriable.Retry(async () => ...)</c> binds to <c>Retry&lt;Task&gt;</c> and does
/// not retry a failure after the first <see langword="await"/>, and <c>RetryTask.Factory.StartNew(async () => ...)</c>
/// runs the lambda as <c>async void</c>. These tests compile each call with Roslyn against this build of the library,
/// because a call that the trap catches cannot be in the test source.
/// </para>
/// </remarks>
[TestClass]
public class AsyncLambdaBindingTests
{
    private const string ObsoleteAsError = "CS0619";

    private const string AsyncRetryMessage = "Use RetryAsync";
    private const string NestedTaskMessage = "Await the task in the operation.";

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    /// <summary>
    /// Gets a call to each public <c>Retry</c> and <c>RetryAsync</c> overload, with the arguments that the overload
    /// takes, and an operation of each asynchronous return type that the overload must not accept.
    /// </summary>
    public static IEnumerable<object[]> GeneratedAsynchronousCalls
    {
        get
        {
            var calls = new HashSet<string>(StringComparer.Ordinal);
            foreach (MethodInfo method in RetriableOverloads.All)
            {
                bool isRetry = RetriableOverloads.IsSynchronous(method);
                string parameter = RetriableOverloads.OperationTakesToken(method) ? "ct" : "()";
                string[] operations = isRetry
                    ?
                    [
                        $"{parameter} => Task.CompletedTask",
                        $"{parameter} => Task.FromResult(1)",
                        $"{parameter} => default(ValueTask)",
                        $"{parameter} => new ValueTask<int>(1)",
                        $"async {parameter} => await Task.Yield()",
                        $"async {parameter} => {{ await Task.Yield(); return 1; }}",
                        $"async ValueTask {parameter} => await Task.Yield()",
                        $"async ValueTask<int> {parameter} => {{ await Task.Yield(); return 1; }}",
                    ]
                    :
                    [
                        $"{parameter} => Task.FromResult(Task.CompletedTask)",
                        $"{parameter} => Task.FromResult(Task.FromResult(1))",
                        $"async {parameter} => Task.CompletedTask",
                        $"async {parameter} => Task.FromResult(1)",
                    ];

                foreach (string arguments in OtherArguments(method))
                {
                    foreach (string operation in operations)
                    {
                        string call = $"Retriable.{method.Name}({operation}{arguments});";
                        if (calls.Add(call))
                        {
                            yield return [call, isRetry ? AsyncRetryMessage : NestedTaskMessage];
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets a call to each public <c>Retry</c> and <c>RetryAsync</c> overload with an operation that it accepts, and a
    /// description of that overload.
    /// </summary>
    public static IEnumerable<object[]> GeneratedValidCalls
    {
        get
        {
            foreach (MethodInfo method in RetriableOverloads.All)
            {
                string operation = method.GetParameters()[0].ParameterType switch
                {
                    Type t when t == typeof(Action) => "() => { }",
                    Type t when t == typeof(Func<string>) => "() => \"x\"",
                    Type t when t == typeof(Func<Task>) => "async () => await Task.Yield()",
                    Type t when t == typeof(Func<Task<string>>) => "async () => { await Task.Yield(); return \"x\"; }",
                    Type t when t == typeof(Func<CancellationToken, Task>) => "async ct => await Task.Yield()",
                    Type t when t == typeof(Func<CancellationToken, Task<string>>) => "async ct => { await Task.Yield(); return \"x\"; }",
                    Type t => throw new NotSupportedException($"No operation is defined for {t}."),
                };

                // The last form has every argument, so that the call binds to this overload rather than a shorter one.
                string arguments = OtherArguments(method).Last();
                yield return [$"Retriable.{method.Name}({operation}{arguments});", RetriableOverloads.Describe(method)];
            }
        }
    }

    [TestMethod]
    [DataRow("Retriable.Retry(async () => await Task.Yield());")]
    [DataRow("Retriable.Retry(async () => { await Task.Yield(); return 1; });")]
    [DataRow("Retriable.Retry(() => Task.CompletedTask);")]
    [DataRow("Retriable.Retry(async () => await Task.Yield(), CancellationToken.None);")]
    [DataRow("Retriable.Retry(async () => await Task.Yield(), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("Retriable.Retry(async () => { await Task.Yield(); return 1; }, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("Retriable.Retry(async () => await Task.Yield(), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, _ => ValueTask.CompletedTask);")]
    [DataRow("Retriable.Retry(async ValueTask () => await Task.Yield());")]
    [DataRow("Retriable.Retry(async ValueTask<int> () => { await Task.Yield(); return 1; });")]
    [DataRow("Retriable.Retry(() => new ValueTask<int>(1));")]
    [DataRow("Retriable.Retry(() => default(ValueTask));")]
    [DataRow("Retriable.Retry(() => default(ValueTask), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("Retriable.Retry(() => default(ValueTask), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, _ => ValueTask.CompletedTask);")]
    [DataRow("Retriable.Retry(() => new ValueTask<int>(1), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, _ => ValueTask.CompletedTask);")]
    [DataRow("Retriable.Retry(() => new ValueTask<int>(1), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("Retriable.Retry(() => default(ValueTask), CancellationToken.None);")]
    public void AnAsynchronousOperationPassedToRetryIsACompileError(string call)
    {
        ImmutableArray<Diagnostic> errors = Compile(call, out _);

        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(ObsoleteAsError, error.Id);
        Assert.Contains("Use RetryAsync", error.GetMessage());
    }

    [TestMethod]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield());")]
    [DataRow("RetryTask.Factory.StartNew(() => Task.CompletedTask);")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), CancellationToken.None);")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), TaskCreationOptions.None);")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), AnyExceptionPolicy.Default);")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), new Count(3));")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("RetryTask.Factory.StartNew(async () => await Task.Yield(), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("RetryTask.Factory.StartNew(async state => await Task.Yield(), 42);")]
    [DataRow("RetryTask.Factory.StartNew(async state => await Task.Yield(), 42, new Count(3), AnyExceptionPolicy.Default);")]
    [DataRow("RetryTask.Factory.StartNew(async state => await Task.Yield(), 42, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);")]
    [DataRow("RetryTask.Factory.StartNew(async ValueTask () => await Task.Yield());")]
    [DataRow("RetryTask.Factory.StartNew(() => default(ValueTask));")]
    [DataRow("RetryTask.Factory.StartNew(() => new ValueTask<int>(1), new Count(3));")]
    [DataRow("RetryTask.Factory.StartNew(state => default(ValueTask), 42);")]
    [DataRow("RetryTask.Factory.StartNew(state => new ValueTask<int>(1), 42, new Count(3), AnyExceptionPolicy.Default);")]
    public void AnAsynchronousActionPassedToStartNewIsACompileError(string call)
    {
        ImmutableArray<Diagnostic> errors = Compile(call, out _);

        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(ObsoleteAsError, error.Id);

        // The message names the methods that do retry an asynchronous action: Run or RunValueTask, and RetryAsync.
        Assert.Contains("Use Run", error.GetMessage());
        Assert.Contains("or Retriable.RetryAsync.", error.GetMessage());
    }

    [TestMethod]
    [DataRow("factory.RunValueTask(async ct => Task.CompletedTask);", "RunValueTask received an operation that returns a task inside its ValueTask.")]
    [DataRow("factory.RunValueTask(async ct => Task.FromResult(1), CancellationToken.None);", "RunValueTask received an operation that returns a task inside its ValueTask.")]
    [DataRow("factory.RunValueTask(async ct => { await Task.Yield(); return Task.CompletedTask; });", "RunValueTask received an operation that returns a task inside its ValueTask.")]
    [DataRow("factory.Run(async ct => Task.CompletedTask);", "Run received an operation that returns a task inside its task.")]
    [DataRow("factory.Run(async ct => Task.FromResult(1), CancellationToken.None);", "Run received an operation that returns a task inside its task.")]
    [DataRow("factory.Run(async ct => { await Task.Yield(); return Task.CompletedTask; });", "Run received an operation that returns a task inside its task.")]
    [DataRow("factory.Run(ct => Task.FromResult(Task.CompletedTask));", "Run received an operation that returns a task inside its task.")]
    [DataRow("RetryTask.Run(async ct => Task.CompletedTask);", "Run received an operation that returns a task inside its task.")]
    [DataRow("RetryTask.Run(async ct => Task.FromResult(1), CancellationToken.None);", "Run received an operation that returns a task inside its task.")]
    [DataRow("RetryTask.Run(ct => Task.FromResult(Task.CompletedTask));", "Run received an operation that returns a task inside its task.")]
    [DataRow("hedged.Run(async ct => Task.CompletedTask);", "Run received an operation that returns a task inside its task.")]
    [DataRow("hedged.Run(async ct => Task.FromResult(1), CancellationToken.None);", "Run received an operation that returns a task inside its task.")]
    [DataRow("hedged.Run(ct => Task.FromResult(Task.CompletedTask));", "Run received an operation that returns a task inside its task.")]
    [DataRow("HedgedTask.Run(async ct => Task.CompletedTask);", "Run received an operation that returns a task inside its task.")]
    [DataRow("HedgedTask.Run(async ct => Task.FromResult(1), CancellationToken.None);", "Run received an operation that returns a task inside its task.")]
    [DataRow("HedgedTask.Run(ct => Task.FromResult(Task.CompletedTask));", "Run received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.FromResult(1));", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, true);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(() => Task.FromResult(Task.CompletedTask));", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, CancellationToken.None);", "RetryAsync received an operation that returns a task inside its task.")]
    public void AnOperationThatReturnsATaskInsideItsTaskIsACompileError(string call, string expectedMessage)
    {
        ImmutableArray<Diagnostic> errors = Compile(call, out _);

        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(ObsoleteAsError, error.Id);
        Assert.Contains(expectedMessage, error.GetMessage());
        Assert.Contains("Await the task in the operation.", error.GetMessage());
    }

    [TestMethod]
    [DataRow("Retriable.Retry(() => 1);", "System.Func<T>")]
    [DataRow("Retriable.Retry(() => new object());", "System.Func<T>")]
    [DataRow("Retriable.Retry(() => { throw new InvalidOperationException(); }, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, _ => ValueTask.CompletedTask);", "System.Action")]
    [DataRow("Retriable.Retry(() => { });", "System.Action")]
    [DataRow("Retriable.Retry(() => { throw new InvalidOperationException(); });", "System.Action")]
    [DataRow("Retriable.Retry(() => { }, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "System.Action")]
    [DataRow("Retriable.RetryAsync(async () => await Task.Yield());", "System.Func<System.Threading.Tasks.Task>")]
    [DataRow("Retriable.RetryAsync(async () => { await Task.Yield(); return 1; });", "System.Func<System.Threading.Tasks.Task<T>>")]
    [DataRow("Retriable.RetryAsync(async () => { await Task.Yield(); return \"x\"; });", "System.Func<System.Threading.Tasks.Task<T>>")]
    [DataRow("Retriable.RetryAsync(async ct => { await Task.Yield(); return 1; }, CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("Retriable.RetryAsync(() => { throw new InvalidOperationException(); });", "System.Func<System.Threading.Tasks.Task>")]
    [DataRow("Retriable.Retry(() => 1, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, _ => ValueTask.CompletedTask);", "System.Func<T>")]
    [DataRow("Retriable.RetryAsync(async () => { await Task.Yield(); return 1; }, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "System.Func<System.Threading.Tasks.Task<T>>")]
    [DataRow("Retriable.RetryAsync(async () => await Task.Yield(), true);", "System.Func<System.Threading.Tasks.Task>")]
    [DataRow("Retriable.RetryAsync(async ct => await Task.Yield(), CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("Retriable.RetryAsync(async ct => await Task.Yield(), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("Retriable.RetryAsync(async ct => await Task.Yield(), CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("factory.RunValueTask(ct => new ValueTask<int>(1));", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<T>>")]
    [DataRow("factory.RunValueTask(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<T>>")]
    [DataRow("factory.RunValueTask(ct => ValueTask.CompletedTask);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>")]
    [DataRow("factory.RunValueTask(async ct => await Task.Yield());", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>")]
    [DataRow("factory.Run(ct => Task.FromResult(1));", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("factory.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("factory.Run(async ct => { await Task.Yield(); return 1; }, CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("factory.Run(ct => Task.CompletedTask);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("factory.Run(async ct => await Task.Yield());", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("factory.Run(async ct => await Task.Yield(), CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("factoryOfT.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("factoryOfT.Run(ct => Task.FromResult(1), CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("factoryOfT.RunValueTask(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<T>>")]
    [DataRow("RetryTask.Run(async ct => await Task.Yield());", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("RetryTask.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("RetryTask<int>.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("hedged.Run(async ct => await Task.Yield());", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("hedged.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("hedged.Run(ct => Task.FromResult(1), CancellationToken.None);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("hedgedOfT.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("HedgedTask.Run(async ct => await Task.Yield());", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>")]
    [DataRow("HedgedTask.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("HedgedTask<int>.Run(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>>")]
    [DataRow("RetryTask.Factory.StartNew(() => { });", "System.Action")]
    [DataRow("RetryTask.Factory.StartNew(() => { throw new InvalidOperationException(); }, new Count(5), AnyExceptionPolicy.Default);", "System.Action")]
    [DataRow("RetryTask.Factory.StartNew(state => { }, 42);", "System.Action<object?>")]
    [DataRow("RetryTask.Factory.StartNew(state => { throw new InvalidOperationException(); }, 42);", "System.Action<object?>")]
    [DataRow("RetryTask<int>.Factory.StartNew(() => 1);", "System.Func<T>")]
    public void ASynchronousOperationBindsAsBefore(string call, string expectedFirstParameterType)
    {
        ImmutableArray<Diagnostic> errors = Compile(call, out IMethodSymbol? method);

        Assert.IsEmpty(errors);
        Assert.IsNotNull(method);
        Assert.IsEmpty(method.OriginalDefinition.GetAttributes().Where(a => a.AttributeClass?.Name == nameof(ObsoleteAttribute)));
        Assert.AreEqual(expectedFirstParameterType, method.OriginalDefinition.Parameters[0].Type.ToDisplayString());
    }

    [TestMethod]
    [DynamicData(nameof(GeneratedAsynchronousCalls))]
    public void EveryNonObsoleteRetryOverloadThatTakesAnOperationHasATrapForEachAsyncReturnType(string call, string expectedMessage)
    {
        ImmutableArray<Diagnostic> errors = Compile(call, out IMethodSymbol? method);

        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(ObsoleteAsError, error.Id);
        Assert.Contains(expectedMessage, error.GetMessage());
        Assert.IsNotNull(method);
        Assert.IsNotEmpty(method.OriginalDefinition.GetAttributes().Where(a => a.AttributeClass?.Name == nameof(ObsoleteAttribute)));
    }

    [TestMethod]
    [DynamicData(nameof(GeneratedValidCalls))]
    public void EveryNonObsoleteRetryOverloadIsReachedByAnOperationThatItAccepts(string call, string overload)
    {
        MethodInfo expected = RetriableOverloads.Find(overload);

        ImmutableArray<Diagnostic> errors = Compile(call, out IMethodSymbol? method);

        Assert.IsEmpty(errors);
        Assert.IsNotNull(method);
        Assert.AreEqual(expected.Name, method.Name);
        Assert.AreSequenceEqual(expected.GetParameters().Select(p => p.Name).ToArray(), method.OriginalDefinition.Parameters.Select(p => p.Name).ToArray());
        Assert.AreEqual(expected.GetParameters()[0].ParameterType.Name, method.OriginalDefinition.Parameters[0].Type.MetadataName);

        // Func<Task> and Func<Task<T>> have the same parameter names; only the generic overload has a type parameter.
        Assert.AreEqual(expected.IsGenericMethod, method.OriginalDefinition.IsGenericMethod);
    }

    // The arguments after the operation, with a leading comma: once without an optional onRetry, and once with it.
    private static IEnumerable<string> OtherArguments(MethodInfo method)
    {
        var required = new List<string>();
        string? optional = null;
        foreach (ParameterInfo parameter in method.GetParameters().Skip(1))
        {
            string argument = parameter.ParameterType switch
            {
                Type t when t == typeof(CancellationToken) => "CancellationToken.None",
                Type t when t == typeof(IRetryStrategy) => "new Count(3)",
                Type t when t == typeof(IRetryPolicy) => "AnyExceptionPolicy.Default",
                Type t when t == typeof(bool) => "true",
                Type t when t == typeof(Func<RetryEventArgs, ValueTask>) => "_ => ValueTask.CompletedTask",
                Type t => throw new NotSupportedException($"No argument is defined for {t}."),
            };

            if (parameter.HasDefaultValue)
            {
                optional = argument;
            }
            else
            {
                required.Add(argument);
            }
        }

        string prefix = string.Concat(required.Select(a => $", {a}"));
        yield return prefix;
        if (optional is not null)
        {
            yield return $"{prefix}, {optional}";
        }
    }

    private static ImmutableArray<Diagnostic> Compile(string call, out IMethodSymbol? method)
    {
        string source = $$"""
            #nullable enable
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Corvus.Retry;
            using Corvus.Retry.Policies;
            using Corvus.Retry.Strategies;

            public static class Caller
            {
                public static void Call()
                {
                    RetryTaskFactory factory = null!;
                    RetryTaskFactory<int> factoryOfT = null!;
                    HedgedTaskFactory hedged = null!;
                    HedgedTaskFactory<int> hedgedOfT = null!;
                    {{call}}
                }
            }
            """;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "AsyncLambdaBinding",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The call is the outermost invocation in the method body.
        InvocationExpressionSyntax invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First();
        method = compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

        return
        [
            .. platform.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(Retriable).Assembly.Location),
        ];
    }
}
