// <copyright file="AssemblySetup.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using Corvus.Retry.Async;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Installs a sleep service that fails any test which would otherwise block on a real delay.
    /// Tests that need to observe delays use <see cref="SleepServiceFixture.InstallRecording"/> in a [DoNotParallelize] class.
    /// </summary>
    [TestClass]
    public static class AssemblySetup
    {
        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            _ = context;
            SleepServiceFixture.InstallForbidden();
        }
    }
}
