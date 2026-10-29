// <copyright file="SleepServiceTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The default sleep service really waits. These are the only tests in the suite that sleep for real.
/// </summary>
[TestClass]
[DoNotParallelize]
public class SleepServiceTests
{
    private static readonly TimeSpan Requested = TimeSpan.FromMilliseconds(25);

    // Timer resolution can make a wait finish fractionally early, so allow a little slack.
    private static readonly TimeSpan MinimumExpected = TimeSpan.FromMilliseconds(20);

    [TestInitialize]
    public void Initialize() => SleepServiceFixture.InstallDefault();

    [TestCleanup]
    public void Cleanup() => SleepServiceFixture.InstallForbidden();

    [TestMethod]
    public void WhenTheTestServiceIsUnset_TheDefaultServiceSleepsForAtLeastTheRequestedTime()
    {
        var stopwatch = Stopwatch.StartNew();

        SleepService.Instance.Sleep(Requested);

        Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task WhenTheTestServiceIsUnset_TheDefaultServiceSleepsAsynchronouslyForAtLeastTheRequestedTime()
    {
        var stopwatch = Stopwatch.StartNew();

        await SleepService.Instance.SleepAsync(Requested).ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(MinimumExpected, stopwatch.Elapsed);
    }
}
