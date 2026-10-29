// <copyright file="ReliabilityPipelineOptionsTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading;
using System.Threading.Tasks;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <see cref="ReliabilityPipelineOptions"/> holds all the settings of a pipeline, has documented defaults, makes changed
/// copies with a <see langword="with"/> expression, and is checked by the constructor of each pipeline, which names
/// the property that is not correct.
/// </summary>
[TestClass]
public class ReliabilityPipelineOptionsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void TheDefaultsAreTheDocumentedValues()
    {
        var options = new ReliabilityPipelineOptions<int>();

        Assert.IsNull(options.Strategy);
        Assert.AreSame(AnyExceptionPolicy.Default, options.Policy);
        Assert.IsNull(options.AttemptTimeout);
        Assert.IsNull(options.TotalTimeout);
        Assert.IsNull(options.OnRetry);
        Assert.AreSame(TimeProvider.System, options.TimeProvider);
        Assert.IsFalse(options.ContinueOnCapturedContext);
        Assert.IsNull(options.CircuitBreaker);
        Assert.IsNull(options.Hedging);
        Assert.IsNull(options.ShouldRetryResult);
        Assert.IsNull(options.Fallback);
        Assert.IsNull(options.HedgingActionGenerator);
    }

    [TestMethod]
    public void TheDefaultsOfTheNonGenericOptionsAreTheDocumentedValues()
    {
        var options = new ReliabilityPipelineOptions();

        Assert.IsNull(options.Strategy);
        Assert.AreSame(AnyExceptionPolicy.Default, options.Policy);
        Assert.IsNull(options.AttemptTimeout);
        Assert.IsNull(options.TotalTimeout);
        Assert.IsNull(options.OnRetry);
        Assert.IsNull(options.CircuitBreaker);
        Assert.IsNull(options.Hedging);
        Assert.AreSame(TimeProvider.System, options.TimeProvider);
        Assert.IsFalse(options.ContinueOnCapturedContext);
    }

    [TestMethod]
    public void AWithExpressionMakesAChangedCopyAndKeepsTheOriginal()
    {
        Func<int, bool> shouldRetryResult = r => r < 0;
        var original = new ReliabilityPipelineOptions<int> { Strategy = new Count(3), ShouldRetryResult = shouldRetryResult };

        ReliabilityPipelineOptions copy = original with { AttemptTimeout = TimeSpan.FromSeconds(1) };

        // The copy has the type of the original, even through a reference to the base type.
        var typedCopy = Assert.IsInstanceOfType<ReliabilityPipelineOptions<int>>(copy);
        Assert.AreSame(original.Strategy, typedCopy.Strategy);
        Assert.AreSame(shouldRetryResult, typedCopy.ShouldRetryResult);
        Assert.AreEqual(TimeSpan.FromSeconds(1), typedCopy.AttemptTimeout);
        Assert.IsNull(original.AttemptTimeout);
    }

    // REQ-09
    [TestMethod]
    public async Task AReferencePipelineWithExponentialDelaysWithJitterAndAnAttemptTimeoutUsesOptionsOnly()
    {
        RecordingFakeTimeProvider fake = new();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            Strategy = RetryStrategies.Exponential(3, TimeSpan.FromMilliseconds(200), useJitter: true),
            AttemptTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = fake,
        });
        var op = new FlakyOperation(1);

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The first delay is between 200 ms and 3 x 200 ms. The timer for the first attempt timeout comes first.
        Assert.IsFalse(call.IsCompleted);
        Assert.HasCount(2, fake.Delays);
        Assert.AreEqual(TimeSpan.FromSeconds(5), fake.Delays[0]);
        Assert.IsInRange(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(600), fake.Delays[1]);
        fake.Advance(fake.Delays[1]);

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    // REQ-09
    [TestMethod]
    public async Task AReferencePipelineWithATotalTimeoutAConstantDelayAndAnAttemptTimeoutUsesOptionsOnly()
    {
        RecordingFakeTimeProvider fake = new();
        var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
        {
            TotalTimeout = TimeSpan.FromSeconds(30),
            Strategy = RetryStrategies.Constant(5, TimeSpan.FromSeconds(1)),
            AttemptTimeout = TimeSpan.FromSeconds(10),
            TimeProvider = fake,
        });
        var op = new FlakyOperation(1);

        Task<string> call = pipeline.ExecuteAsync(_ => new ValueTask<string>(op.Invoke())).AsTask();

        // The timers: the total timeout, the first attempt timeout, and the delay.
        Assert.IsFalse(call.IsCompleted);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1) }, fake.Delays);
        fake.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual("attempt-2", await call.WaitAsync(WaitLimit).ConfigureAwait(false));
    }

    // REQ-09
    [TestMethod]
    [DataRow(nameof(ReliabilityPipelineOptions.AttemptTimeout), "zero")]
    [DataRow(nameof(ReliabilityPipelineOptions.AttemptTimeout), "negative")]
    [DataRow(nameof(ReliabilityPipelineOptions.AttemptTimeout), "infinite")]
    [DataRow(nameof(ReliabilityPipelineOptions.AttemptTimeout), "too long")]
    [DataRow(nameof(ReliabilityPipelineOptions.TotalTimeout), "zero")]
    [DataRow(nameof(ReliabilityPipelineOptions.TotalTimeout), "negative")]
    [DataRow(nameof(ReliabilityPipelineOptions.TotalTimeout), "infinite")]
    [DataRow(nameof(ReliabilityPipelineOptions.TotalTimeout), "too long")]
    public void TheConstructorsRejectATimeoutThatIsOutOfRange(string property, string value)
    {
        TimeSpan timeout = value switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromSeconds(-1),
            "infinite" => Timeout.InfiniteTimeSpan,
            _ => RetryDelay.MaxSupported + TimeSpan.FromMilliseconds(1),
        };

        ReliabilityPipelineOptions WithTimeout(ReliabilityPipelineOptions options) => property == nameof(ReliabilityPipelineOptions.AttemptTimeout)
            ? options with { AttemptTimeout = timeout }
            : options with { TotalTimeout = timeout };

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ReliabilityPipeline(WithTimeout(new ReliabilityPipelineOptions())));
        ArgumentOutOfRangeException thrownOfT = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ReliabilityPipeline<int>((ReliabilityPipelineOptions<int>)WithTimeout(new ReliabilityPipelineOptions<int>())));

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
        Assert.AreEqual(timeout, thrown.ActualValue);
        Assert.AreEqual(timeout, thrownOfT.ActualValue);
    }

    // REQ-09
    [TestMethod]
    [DataRow(nameof(ReliabilityPipelineOptions.Policy))]
    [DataRow(nameof(ReliabilityPipelineOptions.TimeProvider))]
    public void TheConstructorsRejectANullProperty(string property)
    {
        ReliabilityPipelineOptions WithNull(ReliabilityPipelineOptions options) => property == nameof(ReliabilityPipelineOptions.Policy)
            ? options with { Policy = null! }
            : options with { TimeProvider = null! };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(
            () => new ReliabilityPipeline(WithNull(new ReliabilityPipelineOptions())));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(
            () => new ReliabilityPipeline<int>((ReliabilityPipelineOptions<int>)WithNull(new ReliabilityPipelineOptions<int>())));

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
    }

    // REQ-09
    [TestMethod]
    public void TheConstructorsRejectNullOptions()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new ReliabilityPipeline(null!));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(() => new ReliabilityPipeline<int>(null!));

        Assert.AreEqual("options", thrown.ParamName);
        Assert.AreEqual("options", thrownOfT.ParamName);
    }

    [TestMethod]
    [DataRow(nameof(ReliabilityPipelineOptions<>.ShouldRetryResult))]
    [DataRow(nameof(ReliabilityPipelineOptions<>.Fallback))]
    [DataRow(nameof(ReliabilityPipelineOptions<>.HedgingActionGenerator))]
    public void TheNonGenericPipelineRejectsGenericOptionsThatHaveAResultSetting(string setting)
    {
        ReliabilityPipelineOptions<int> options = setting switch
        {
            nameof(ReliabilityPipelineOptions<>.ShouldRetryResult) => new() { ShouldRetryResult = r => r < 0 },
            nameof(ReliabilityPipelineOptions<>.Fallback) => new() { Fallback = new FallbackOptions<int> { Action = _ => ValueTask.FromResult(0) } },
            _ => new() { Hedging = new HedgingOptions(), HedgingActionGenerator = _ => null },
        };

        // The non-generic pipeline cannot use a setting for a result, so it must not ignore the setting.
        ArgumentException thrown = Assert.ThrowsExactly<ArgumentException>(() => new ReliabilityPipeline(options));

        Assert.AreEqual("options", thrown.ParamName);
    }

    [TestMethod]
    public async Task TheNonGenericPipelineAcceptsGenericOptionsWithoutAResultSetting()
    {
        ReliabilityPipelineOptions<int> options = new() { Strategy = new Count(2) };
        int invocations = 0;

        var pipeline = new ReliabilityPipeline(options);
        await pipeline.ExecuteAsync(_ =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                throw new InvalidOperationException("First attempt failed");
            }

            return ValueTask.CompletedTask;
        }).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    public async Task TheGenericPipelineAcceptsAResultSetting()
    {
        ReliabilityPipelineOptions<int> options = new()
        {
            Strategy = new Count(2),
            ShouldRetryResult = r => r < 0,
            Fallback = new FallbackOptions<int> { Action = _ => ValueTask.FromResult(99) },
        };
        int invocations = 0;

        var pipeline = new ReliabilityPipeline<int>(options);
        int result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(Interlocked.Increment(ref invocations) == 1 ? -1 : 1)).AsTask().WaitAsync(WaitLimit).ConfigureAwait(false);

        // The predicate rejected the first result, so the pipeline used the setting and tried again.
        Assert.AreEqual(1, result);
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    [DataRow("one tick")]
    [DataRow("longest")]
    public void TheConstructorsAcceptTheShortestAndTheLongestTimeouts(string value)
    {
        TimeSpan timeout = value == "one tick" ? TimeSpan.FromTicks(1) : RetryDelay.MaxSupported;

        _ = new ReliabilityPipeline(new ReliabilityPipelineOptions { AttemptTimeout = timeout, TotalTimeout = timeout });
        _ = new ReliabilityPipeline<int>(new ReliabilityPipelineOptions<int> { AttemptTimeout = timeout, TotalTimeout = timeout });
    }
}
