// <copyright file="AsyncLambdaBindingTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// An asynchronous lambda passed to <see cref="Retriable.Retry{T}(Func{T})"/> or to
/// <see cref="RetryTaskFactory.StartNew(Action)"/> is a compile error, and synchronous lambdas bind as before.
/// An operation that returns a task inside its task, passed to <c>RetryAsync</c> or
/// <see cref="ReliabilityPipeline.ExecuteAsync{T}(Func{System.Threading.CancellationToken, System.Threading.Tasks.ValueTask{T}}, System.Threading.CancellationToken)"/>,
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

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

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
        Assert.Contains("Use Retriable.RetryAsync", error.GetMessage());
    }

    [TestMethod]
    [DataRow("pipeline.ExecuteAsync(async ct => Task.CompletedTask);", "ExecuteAsync received an operation that returns a task inside its ValueTask.")]
    [DataRow("pipeline.ExecuteAsync(async ct => Task.FromResult(1), CancellationToken.None);", "ExecuteAsync received an operation that returns a task inside its ValueTask.")]
    [DataRow("Retriable.RetryAsync(async () => Task.FromResult(1));", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, true);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async () => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default);", "RetryAsync received an operation that returns a task inside its task.")]
    [DataRow("Retriable.RetryAsync(async ct => Task.CompletedTask, CancellationToken.None, new Count(3), AnyExceptionPolicy.Default, false, _ => ValueTask.CompletedTask);", "RetryAsync received an operation that returns a task inside its task.")]
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
    [DataRow("pipeline.ExecuteAsync(ct => new ValueTask<int>(1));", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<T>>")]
    [DataRow("pipeline.ExecuteAsync(async ct => { await Task.Yield(); return 1; });", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<T>>")]
    [DataRow("pipeline.ExecuteAsync(ct => ValueTask.CompletedTask);", "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>")]
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
                    ReliabilityPipeline pipeline = null!;
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
