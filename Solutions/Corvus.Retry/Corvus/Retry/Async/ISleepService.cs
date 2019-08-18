// <copyright file="ISleepService.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// An interface implemented by services which provide the ability to put
    /// a thread to sleep for a period of time.
    /// </summary>
    public interface ISleepService
    {
        /// <summary>
        /// Suspend the thread for a given amount of time.
        /// </summary>
        /// <param name="timeSpan">The period of time for which to suspend the current thread.</param>
        /// <remarks>To reduce the risk of deadlocks if you are in a synchronous context, implementers should
        /// attempt use a low-level "suspend thread" mechanism rather than simple calling their async implementation
        /// and blocking.
        /// </remarks>
        void Sleep(TimeSpan timeSpan);

        /// <summary>
        /// Return a Task which completes after a certain amount of time.
        /// </summary>
        /// <param name="timeSpan">The period of time for which to suspend the current thread.</param>
        /// <returns>A <see cref="Task"/> which completes when the given time has elapsed.</returns>
        /// <remarks>If you are in a synchronous context, prefer the <see cref="Sleep(TimeSpan)"/> method rather
        /// than blocking on this async method with <see cref="Task.Wait()"/> to reduce the risk of deadlocks.</remarks>
        Task SleepAsync(TimeSpan timeSpan);
    }
}