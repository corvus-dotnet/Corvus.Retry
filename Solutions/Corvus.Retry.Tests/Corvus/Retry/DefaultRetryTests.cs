// <copyright file="DefaultRetryTests.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Ensures that running a retriable operation with no additional arguments gives sensible defaults.
/// </summary>
[TestClass]
public class DefaultRetryTests
{
    private int callCount;
    private object? lastObjectReturnedByFunction;
    private object? retryResult;

    [TestMethod]
    [DataRow(RetryActionType.Action)]
    [DataRow(RetryActionType.Function)]
    [DataRow(RetryActionType.AsyncAction)]
    [DataRow(RetryActionType.AsyncActionSpecifyingContinueContext)]
    [DataRow(RetryActionType.AsyncFunction)]
    [DataRow(RetryActionType.AsyncFunctionSpecifyingContinueContext)]
    public async Task ActionSucceedsFirstTime(RetryActionType actionType)
    {
        await this.InvokeAsync(actionType, this.MakeOperation(0)).ConfigureAwait(false);

        Assert.AreEqual(1, this.callCount);
    }

    [TestMethod]
    [DataRow(RetryActionType.Action)]
    [DataRow(RetryActionType.Function)]
    [DataRow(RetryActionType.AsyncAction)]
    [DataRow(RetryActionType.AsyncActionSpecifyingContinueContext)]
    [DataRow(RetryActionType.AsyncFunction)]
    [DataRow(RetryActionType.AsyncFunctionSpecifyingContinueContext)]
    public async Task ActionSucceedsAfterFailures(RetryActionType actionType)
    {
        await this.InvokeAsync(actionType, this.MakeOperation(3)).ConfigureAwait(false);

        Assert.AreEqual(4, this.callCount);

        // Where a value is returned, it should be the one returned by the final operation invocation.
        switch (actionType)
        {
            case RetryActionType.Function:
            case RetryActionType.AsyncFunction:
            case RetryActionType.AsyncFunctionSpecifyingContinueContext:
                Assert.IsNotNull(this.lastObjectReturnedByFunction);
                Assert.AreSame(this.lastObjectReturnedByFunction, this.retryResult);
                break;
        }
    }

    [TestMethod]
    [DataRow(RetryActionType.Action)]
    [DataRow(RetryActionType.Function)]
    [DataRow(RetryActionType.AsyncAction)]
    [DataRow(RetryActionType.AsyncActionSpecifyingContinueContext)]
    [DataRow(RetryActionType.AsyncFunction)]
    [DataRow(RetryActionType.AsyncFunctionSpecifyingContinueContext)]
    public async Task ValidateOperationArgument(RetryActionType actionType)
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => this.InvokeAsync(actionType, null)).ConfigureAwait(false);
    }

    private Func<object> MakeOperation(int timesToThrow)
    {
        return () =>
        {
            this.callCount += 1;
            if (timesToThrow-- > 0)
            {
                throw new InvalidOperationException();
            }

            object o = new();
            this.lastObjectReturnedByFunction = o;
            return o;
        };
    }

    private async Task InvokeAsync(RetryActionType actionType, Func<object>? function)
    {
        Action? action = null;
        Func<Task>? asyncAction = null;
        Func<Task<object>>? asyncFunction = null;
        if (function != null)
        {
            action = () => function();
            asyncAction = () =>
            {
                action();
                return Task.CompletedTask;
            };
            asyncFunction = () => Task.FromResult(function());
        }

        // This code is used when testing the case where the operation is null, therefore the null forgiving
        // operator is intentionally used to accurately describe the intention of the test.
        switch (actionType)
        {
            case RetryActionType.Action:
                Retriable.Retry(action!);
                break;
            case RetryActionType.Function:
                this.retryResult = Retriable.Retry(function!);
                break;
            case RetryActionType.AsyncAction:
                await Retriable.RetryAsync(asyncAction!).ConfigureAwait(false);
                break;
            case RetryActionType.AsyncActionSpecifyingContinueContext:
                await Retriable.RetryAsync(asyncAction!, false).ConfigureAwait(false);
                break;
            case RetryActionType.AsyncFunction:
                this.retryResult = await Retriable.RetryAsync(asyncFunction!).ConfigureAwait(false);
                break;
            case RetryActionType.AsyncFunctionSpecifyingContinueContext:
                this.retryResult = await Retriable.RetryAsync(asyncFunction!, false).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("Unknown action: " + actionType);
        }
    }
}
