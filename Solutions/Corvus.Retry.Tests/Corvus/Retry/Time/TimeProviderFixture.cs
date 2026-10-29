// <copyright file="TimeProviderFixture.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Time;

using System;

/// <summary>
/// Controls the library's process-wide <see cref="RetryDefaults.TimeProvider"/>. Any test class that calls
/// <see cref="Install{T}(T)"/> must be marked [DoNotParallelize] and call <see cref="Reset"/> in [TestCleanup].
/// </summary>
internal static class TimeProviderFixture
{
    public static T Install<T>(T timeProvider)
        where T : TimeProvider
    {
        RetryDefaults.TimeProvider = timeProvider;
        return timeProvider;
    }

    public static void Reset()
    {
        RetryDefaults.TimeProvider = TimeProvider.System;
    }
}
