// <copyright file="PolicyBenchmarks.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Corvus.Retry.Policies;

/// <summary>
/// The cost of each built-in policy deciding whether to retry. See <see cref="AggregatePolicyBenchmarks"/>
/// for <see cref="AggregatePolicy"/>.
/// </summary>
/// <remarks>
/// Each policy is created once, so these benchmarks measure only the decision.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class PolicyBenchmarks
{
    private static readonly InvalidOperationException Failure = new("Transient failure");
    private static readonly HttpRequestException HttpFailure = new("Service unavailable", null, HttpStatusCode.ServiceUnavailable);

    // An IOException three levels down: outer -> middle -> inner.
    private static readonly InvalidOperationException NestedFailure = new("Outer", new ArgumentException("Middle", new IOException("Inner")));

    private static readonly HttpResponseMessage TransientResponse = new(HttpStatusCode.ServiceUnavailable);

    private static readonly IRetryPolicy HandlePolicy = RetryPolicies.Handle<HttpRequestException>();
    private static readonly IRetryPolicy HandleWithPredicatePolicy =
        RetryPolicies.Handle<HttpRequestException>(static ex => ex.StatusCode == HttpStatusCode.ServiceUnavailable);

    private static readonly IRetryPolicy HandleOrPolicy = RetryPolicies.Handle<TimeoutException>().Or<HttpRequestException>();
    private static readonly IRetryPolicy HandleInnerPolicy = RetryPolicies.HandleInner<IOException>();
    private static readonly IRetryPolicy FromPredicatePolicy = RetryPolicies.FromPredicate(static ex => ex is InvalidOperationException);
    private static readonly IRetryPolicy NotPolicy = RetryPolicies.Not(new DoNotRetryPolicy());
    private static readonly IRetryPolicy AnyTwoPolicy = RetryPolicies.Any(new DoNotRetryPolicy(), new AnyExceptionPolicy());
    private static readonly IRetryPolicy AllTwoPolicy = RetryPolicies.All(new AnyExceptionPolicy(), new AnyExceptionPolicy());
    private static readonly IRetryPolicy OrExtensionPolicy = new DoNotRetryPolicy().Or(new AnyExceptionPolicy());
    private static readonly IRetryPolicy AndExtensionPolicy = new AnyExceptionPolicy().And(new AnyExceptionPolicy());
    private static readonly IRetryPolicy NotExtensionPolicy = new DoNotRetryPolicy().Not();

    private readonly AnyExceptionPolicy anyException = new();
    private readonly DoNotRetryPolicy doNotRetry = new();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Simple")]
    public bool AnyExceptionPolicy_CanRetry() => this.anyException.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Simple")]
    public bool AnyExceptionPolicy_Default() => AnyExceptionPolicy.Default.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Simple")]
    public bool DoNotRetryPolicy_CanRetry() => this.doNotRetry.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Simple")]
    public bool FromPredicate() => FromPredicatePolicy.CanRetry(Failure);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Handle")]
    public bool Handle_Match() => HandlePolicy.CanRetry(HttpFailure);

    [Benchmark]
    [BenchmarkCategory("Handle")]
    public bool Handle_NoMatch() => HandlePolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Handle")]
    public bool Handle_WithPredicate() => HandleWithPredicatePolicy.CanRetry(HttpFailure);

    [Benchmark]
    [BenchmarkCategory("Handle")]
    public bool Handle_Or_SecondTypeMatch() => HandleOrPolicy.CanRetry(HttpFailure);

    [Benchmark]
    [BenchmarkCategory("Handle")]
    public bool HandleInner_DepthThree() => HandleInnerPolicy.CanRetry(NestedFailure);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Combinators")]
    public bool Not() => NotPolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Combinators")]
    public bool Any_Two() => AnyTwoPolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Combinators")]
    public bool All_Two() => AllTwoPolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Combinators")]
    public bool Extension_Or() => OrExtensionPolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Combinators")]
    public bool Extension_And() => AndExtensionPolicy.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Combinators")]
    public bool Extension_Not() => NotExtensionPolicy.CanRetry(Failure);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Http")]
    public bool TransientHttp_HttpRequestException() => RetryPolicies.TransientHttp.CanRetry(HttpFailure);

    [Benchmark]
    [BenchmarkCategory("Http")]
    public bool TransientHttp_Other() => RetryPolicies.TransientHttp.CanRetry(Failure);

    [Benchmark]
    [BenchmarkCategory("Http")]
    public bool IsTransientHttpResponse() => RetryPolicies.IsTransientHttpResponse(TransientResponse);
}
