// <copyright file="SleepRecord.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async
{
    using System;
    using System.Threading;

    /// <summary>
    /// A delay requested of a <see cref="RecordingSleepService"/>.
    /// </summary>
    internal sealed class SleepRecord
    {
        public SleepRecord(TimeSpan duration, bool isAsync, CancellationToken token)
        {
            this.Duration = duration;
            this.IsAsync = isAsync;
            this.Token = token;
        }

        public TimeSpan Duration { get; }

        public bool IsAsync { get; }

        /// <summary>
        /// Gets the cancellation token passed with the request.
        /// </summary>
        public CancellationToken Token { get; }
    }
}
