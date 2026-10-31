// <copyright file="RecordingRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A policy that records every exception it is asked about and answers via a delegate.
    /// </summary>
    internal sealed class RecordingRetryPolicy : IRetryPolicy
    {
        private readonly Func<Exception, bool> decide;
        private readonly object sync = new();
        private readonly List<Exception> consulted = new();

        public RecordingRetryPolicy(bool canRetry)
            : this(_ => canRetry)
        {
        }

        public RecordingRetryPolicy(Func<Exception, bool> decide)
        {
            this.decide = decide;
        }

        public IReadOnlyList<Exception> Consulted
        {
            get
            {
                lock (this.sync)
                {
                    return this.consulted.ToArray();
                }
            }
        }

        public bool CanRetry(Exception exception)
        {
            lock (this.sync)
            {
                this.consulted.Add(exception);
            }

            return this.decide(exception);
        }
    }
}
