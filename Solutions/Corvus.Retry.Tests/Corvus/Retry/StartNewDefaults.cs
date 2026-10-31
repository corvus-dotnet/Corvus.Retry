// <copyright file="StartNewDefaults.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// Which of the retry strategy and retry policy a <c>StartNew</c> overload takes. The factory supplies defaults
/// (<c>new Count()</c> and <c>AnyExceptionPolicy</c>) for those it does not take.
/// </summary>
public enum StartNewDefaults
{
    /// <summary>The caller passes both a strategy and a policy.</summary>
    StrategyAndPolicy,

    /// <summary>The caller passes a strategy; the policy defaults.</summary>
    StrategyOnly,

    /// <summary>The caller passes a policy; the strategy defaults.</summary>
    PolicyOnly,

    /// <summary>The caller passes neither; both default.</summary>
    Neither,
}
