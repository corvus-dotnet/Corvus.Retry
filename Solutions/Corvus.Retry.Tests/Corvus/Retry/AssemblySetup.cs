// <copyright file="AssemblySetup.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using Corvus.Retry.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Stops every test in the assembly from waiting for real time through <see cref="RetryDefaults.TimeProvider"/>.
/// </summary>
[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static void InstallTheForbiddenTimeProvider(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TimeProviderFixture.Reset();
    }

    // A test that caught the exception of a forbidden timer still waited for nothing, but it did not do what it meant to.
    [AssemblyCleanup]
    public static void NoTestAskedForARealTimer()
    {
        Assert.IsEmpty(ForbiddenTimeProvider.Instance.Violations, string.Join(Environment.NewLine, ForbiddenTimeProvider.Instance.Violations));
    }
}
