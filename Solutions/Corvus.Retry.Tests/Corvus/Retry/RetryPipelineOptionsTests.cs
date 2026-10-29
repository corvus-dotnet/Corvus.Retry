// <copyright file="RetryPipelineOptionsTests.cs" company="Endjin Limited">
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
/// <see cref="RetryPipelineOptions"/> holds all the settings of a pipeline, has documented defaults, makes changed
/// copies with a <see langword="with"/> expression, and is checked by the constructor of each pipeline, which names
/// the property that is not correct.
/// </summary>
[TestClass]
public class RetryPipelineOptionsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void TheDefaultsAreTheDocumentedValues()
    {
        var options = new RetryPipelineOptions<int>();

        Assert.IsNull(options.Strategy);
        Assert.AreSame(AnyExceptionPolicy.Default, options.Policy);
        Assert.IsNull(options.AttemptTimeout);
        Assert.IsNull(options.TotalTimeout);
        Assert.IsNull(options.OnRetry);
        Assert.AreSame(TimeProvider.System, options.TimeProvider);
        Assert.IsFalse(options.ContinueOnCapturedContext);
        Assert.IsNull(options.ShouldRetryResult);
    }

    [TestMethod]
    public void AWithExpressionMakesAChangedCopyAndKeepsTheOriginal()
    {
        Func<int, bool> shouldRetryResult = r => r < 0;
        var original = new RetryPipelineOptions<int> { Strategy = new Count(3), ShouldRetryResult = shouldRetryResult };

        RetryPipelineOptions copy = original with { AttemptTimeout = TimeSpan.FromSeconds(1) };

        // The copy has the type of the original, even through a reference to the base type.
        var typedCopy = Assert.IsInstanceOfType<RetryPipelineOptions<int>>(copy);
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
        var pipeline = new RetryPipeline(new RetryPipelineOptions
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
        var pipeline = new RetryPipeline(new RetryPipelineOptions
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
    [DataRow(nameof(RetryPipelineOptions.AttemptTimeout), "zero")]
    [DataRow(nameof(RetryPipelineOptions.AttemptTimeout), "negative")]
    [DataRow(nameof(RetryPipelineOptions.AttemptTimeout), "infinite")]
    [DataRow(nameof(RetryPipelineOptions.AttemptTimeout), "too long")]
    [DataRow(nameof(RetryPipelineOptions.TotalTimeout), "zero")]
    [DataRow(nameof(RetryPipelineOptions.TotalTimeout), "negative")]
    [DataRow(nameof(RetryPipelineOptions.TotalTimeout), "infinite")]
    [DataRow(nameof(RetryPipelineOptions.TotalTimeout), "too long")]
    public void TheConstructorsRejectATimeoutThatIsOutOfRange(string property, string value)
    {
        TimeSpan timeout = value switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromSeconds(-1),
            "infinite" => Timeout.InfiniteTimeSpan,
            _ => RetryDelay.MaxSupported + TimeSpan.FromMilliseconds(1),
        };

        RetryPipelineOptions WithTimeout(RetryPipelineOptions options) => property == nameof(RetryPipelineOptions.AttemptTimeout)
            ? options with { AttemptTimeout = timeout }
            : options with { TotalTimeout = timeout };

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RetryPipeline(WithTimeout(new RetryPipelineOptions())));
        ArgumentOutOfRangeException thrownOfT = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RetryPipeline<int>((RetryPipelineOptions<int>)WithTimeout(new RetryPipelineOptions<int>())));

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
        Assert.AreEqual(timeout, thrown.ActualValue);
    }

    // REQ-09
    [TestMethod]
    [DataRow(nameof(RetryPipelineOptions.Policy))]
    [DataRow(nameof(RetryPipelineOptions.TimeProvider))]
    public void TheConstructorsRejectANullProperty(string property)
    {
        RetryPipelineOptions WithNull(RetryPipelineOptions options) => property == nameof(RetryPipelineOptions.Policy)
            ? options with { Policy = null! }
            : options with { TimeProvider = null! };

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(
            () => new RetryPipeline(WithNull(new RetryPipelineOptions())));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(
            () => new RetryPipeline<int>((RetryPipelineOptions<int>)WithNull(new RetryPipelineOptions<int>())));

        Assert.AreEqual(property, thrown.ParamName);
        Assert.AreEqual(property, thrownOfT.ParamName);
    }

    // REQ-09
    [TestMethod]
    public void TheConstructorsRejectNullOptions()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryPipeline(null!));
        ArgumentNullException thrownOfT = Assert.ThrowsExactly<ArgumentNullException>(() => new RetryPipeline<int>(null!));

        Assert.AreEqual("options", thrown.ParamName);
        Assert.AreEqual("options", thrownOfT.ParamName);
    }

    [TestMethod]
    [DataRow("one tick")]
    [DataRow("longest")]
    public void TheConstructorsAcceptTheShortestAndTheLongestTimeouts(string value)
    {
        TimeSpan timeout = value == "one tick" ? TimeSpan.FromTicks(1) : RetryDelay.MaxSupported;

        _ = new RetryPipeline(new RetryPipelineOptions { AttemptTimeout = timeout, TotalTimeout = timeout });
        _ = new RetryPipeline<int>(new RetryPipelineOptions<int> { AttemptTimeout = timeout, TotalTimeout = timeout });
    }
}
