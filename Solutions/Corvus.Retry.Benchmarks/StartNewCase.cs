// <copyright file="StartNewCase.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Benchmarks
{
    /// <summary>
    /// Identifies one public <c>StartNew</c> overload, as a BenchmarkDotNet parameter.
    /// </summary>
    public sealed class StartNewCase
    {
        public StartNewCase(StartNewOverload overload, StartNewDefaults defaults)
        {
            this.Overload = overload;
            this.Defaults = defaults;
        }

        /// <summary>
        /// Gets the task-related parameters the overload takes.
        /// </summary>
        public StartNewOverload Overload { get; }

        /// <summary>
        /// Gets which of the strategy and the policy the overload takes.
        /// </summary>
        public StartNewDefaults Defaults { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{this.Overload}/{this.Defaults}";
    }
}
