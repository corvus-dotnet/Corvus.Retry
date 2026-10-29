// <copyright file="SleepServiceFixture.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async;

using System;
using System.Threading.Tasks;

/// <summary>
/// Controls the library's process-wide sleep service. Any test class calling <see cref="InstallRecording"/>
/// or <see cref="InstallDefault"/> must be marked [DoNotParallelize] and restore with <see cref="InstallForbidden"/> in [TestCleanup].
/// </summary>
internal static class SleepServiceFixture
{
    public static RecordingSleepService InstallRecording()
    {
        RecordingSleepService service = new();
        SleepService.SetSleepServiceForTesting(service);
        return service;
    }

    public static void InstallForbidden()
    {
        SleepService.SetSleepServiceForTesting(ForbiddenSleepService.Instance);
    }

    public static void InstallDefault()
    {
        SleepService.UnsetSleepServiceForTesting();
    }

    private sealed class ForbiddenSleepService : ISleepService
    {
        public static readonly ForbiddenSleepService Instance = new();

        public void Sleep(TimeSpan timeSpan)
        {
            throw Forbidden(timeSpan);
        }

        public Task SleepAsync(TimeSpan timeSpan)
        {
            throw Forbidden(timeSpan);
        }

        private static InvalidOperationException Forbidden(TimeSpan timeSpan)
        {
            return new InvalidOperationException(
                $"A test requested a real sleep of {timeSpan}. Use a zero-delay strategy (Count/DoNotRetry) or install a RecordingSleepService via SleepServiceFixture in a [DoNotParallelize] test class.");
        }
    }
}
