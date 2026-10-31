// <copyright file="StartNewDefaults.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

/// <summary>
/// Which of the retry strategy and retry policy a <c>StartNew</c> overload takes. The factory supplies its own
/// <c>Strategy</c> and <c>Policy</c> for those it does not take: by default, <c>new Count()</c> and <c>AnyExceptionPolicy</c>.
/// </summary>
public enum StartNewDefaults
{
    /// <summary>The caller passes both a strategy and a policy.</summary>
    StrategyAndPolicy,

    /// <summary>The caller passes a strategy; the policy comes from the factory.</summary>
    StrategyOnly,

    /// <summary>The caller passes a policy; the strategy comes from the factory.</summary>
    PolicyOnly,

    /// <summary>The caller passes neither; both come from the factory.</summary>
    Neither,
}
