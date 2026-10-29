// <copyright file="SleepRecord.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async;

using System;

/// <summary>
/// A delay requested of a <see cref="RecordingSleepService"/>.
/// </summary>
internal sealed class SleepRecord
{
    public SleepRecord(TimeSpan duration, bool isAsync)
    {
        this.Duration = duration;
        this.IsAsync = isAsync;
    }

    public TimeSpan Duration { get; }

    public bool IsAsync { get; }
}
