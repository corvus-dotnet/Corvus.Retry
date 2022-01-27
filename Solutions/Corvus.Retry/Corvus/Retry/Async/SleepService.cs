// <copyright file="SleepService.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides access to the sleep service which is used by all retry logic to provide a way to wait.
    /// Tests can replace the default implementation with a fake instance to be able to control time.
    /// </summary>
    internal static class SleepService
    {
        private static ISleepService? instance;

        /// <summary>
        /// Gets a service that provides 'sleep' functionality.
        /// </summary>
        internal static ISleepService Instance
        {
            get => instance ??= new DefaultSleepService();
        }

        /// <summary>
        /// Sets the sleep service to use for testing in order to be able to control time.
        /// </summary>
        /// <param name="testingSleepService">The testing sleep service to use.</param>
        internal static void SetSleepServiceForTesting(ISleepService testingSleepService)
        {
            instance = testingSleepService;
        }

        /// <summary>
        /// Resets the sleep service as no longer being used for testing.
        /// </summary>
        internal static void UnsetSleepServiceForTesting()
        {
            instance = null;
        }

        /// <summary>
        /// Implements the <see cref="ISleepService"/>.
        /// </summary>
        private class DefaultSleepService : ISleepService
        {
            /// <inheritdoc/>
            public void Sleep(TimeSpan timeSpan)
            {
                Thread.Sleep(timeSpan);
            }

            /// <inheritdoc/>
            public Task SleepAsync(TimeSpan timeSpan)
            {
                return Task.Delay(timeSpan);
            }
        }
    }
}