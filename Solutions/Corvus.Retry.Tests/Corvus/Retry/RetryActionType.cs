// <copyright file="RetryActionType.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    public enum RetryActionType
    {
        Action,
        Function,
        AsyncAction,
        AsyncActionSpecifyingContinueContext,
        AsyncFunction,
        AsyncFunctionSpecifyingContinueContext,
    }
}