// <copyright file="SleepService.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Implements the <see cref="ISleepService"/>.
    /// </summary>
    public class SleepService : ISleepService
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