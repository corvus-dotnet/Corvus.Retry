// <copyright file="StubRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;

    /// <summary>
    /// A test double for <see cref="IRetryPolicy"/> that always returns a fixed answer.
    /// </summary>
    internal sealed class StubRetryPolicy : IRetryPolicy
    {
        private readonly bool canRetry;

        public StubRetryPolicy(bool canRetry)
        {
            this.canRetry = canRetry;
        }

        public bool CanRetry(Exception exception) => this.canRetry;
    }
}
