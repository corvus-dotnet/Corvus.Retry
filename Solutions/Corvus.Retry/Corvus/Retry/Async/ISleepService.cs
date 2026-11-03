// <copyright file="ISleepService.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An interface implemented by services which provide the ability to put
    /// a thread to sleep for a period of time.
    /// </summary>
    public interface ISleepService
    {
        /// <summary>
        /// Suspend the thread for a given amount of time, or until cancellation is requested.
        /// </summary>
        /// <param name="timeSpan">The period of time for which to suspend the current thread.</param>
        /// <param name="cancellationToken">The token with which cancellation of the wait is signalled.</param>
        /// <exception cref="OperationCanceledException">
        /// Cancellation was requested before or during the wait. The exception carries <paramref name="cancellationToken"/>.
        /// </exception>
        /// <remarks>To reduce the risk of deadlocks if you are in a synchronous context, implementers should
        /// attempt to use a low-level "suspend thread" mechanism rather than simply calling their async implementation
        /// and blocking.
        /// </remarks>
        void Sleep(TimeSpan timeSpan, CancellationToken cancellationToken);

        /// <summary>
        /// Return a Task which completes after a certain amount of time, or is cancelled when cancellation is requested.
        /// </summary>
        /// <param name="timeSpan">The period of time for which to wait.</param>
        /// <param name="cancellationToken">The token with which cancellation of the wait is signalled.</param>
        /// <returns>
        /// A <see cref="Task"/> which completes when the given time has elapsed. If cancellation is requested before
        /// or during the wait, the task is cancelled, and awaiting it throws an <see cref="OperationCanceledException"/>
        /// which carries <paramref name="cancellationToken"/>.
        /// </returns>
        /// <remarks>If you are in a synchronous context, prefer the <see cref="Sleep(TimeSpan, CancellationToken)"/> method rather
        /// than blocking on this async method with <see cref="Task.Wait()"/> to reduce the risk of deadlocks.</remarks>
        Task SleepAsync(TimeSpan timeSpan, CancellationToken cancellationToken);
    }
}