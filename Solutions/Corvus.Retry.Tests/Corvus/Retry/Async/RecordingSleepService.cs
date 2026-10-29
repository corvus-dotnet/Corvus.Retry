// <copyright file="RecordingSleepService.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Async;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// An <see cref="ISleepService"/> that records every requested delay and returns immediately.
/// </summary>
internal sealed class RecordingSleepService : ISleepService
{
    private readonly object sync = new();
    private readonly List<SleepRecord> records = new();

    /// <summary>
    /// Gets or sets an optional hook invoked on every sleep (used to assert ordering against operation invocations).
    /// </summary>
    public Action<SleepRecord>? OnSleep { get; set; }

    public IReadOnlyList<SleepRecord> Records
    {
        get
        {
            lock (this.sync)
            {
                return this.records.ToArray();
            }
        }
    }

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (this.sync)
            {
                return this.records.ConvertAll(r => r.Duration);
            }
        }
    }

    public void Sleep(TimeSpan timeSpan)
    {
        this.Record(new SleepRecord(timeSpan, isAsync: false));
    }

    public Task SleepAsync(TimeSpan timeSpan)
    {
        this.Record(new SleepRecord(timeSpan, isAsync: true));
        return Task.CompletedTask;
    }

    private void Record(SleepRecord record)
    {
        lock (this.sync)
        {
            this.records.Add(record);
        }

        this.OnSleep?.Invoke(record);
    }
}
