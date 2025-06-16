// <copyright file="DefaultRetrySteps.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using Reqnroll;

    [Binding]
    public class DefaultRetrySteps
    {
        private Func<object>? function;
        private int callCount;
        private object? lastObjectReturnedByFunction;
        private object? retryResult;
        private Exception? exceptionThrownByRetry;

        [Given("I have an operation that succeeds first time")]
        public void GivenIHaveAnOperationThatSucceedsFirstTime()
        {
            this.function = () =>
            {
                this.callCount += 1;
                object o = new();
                this.lastObjectReturnedByFunction = o;
                return o;
            };
        }

        [Given("I have an operation that fails (.*) times then succeeds")]
        public void GivenIHaveAnOperationThatFailsTimesThenSucceeds(int timesToThrow)
        {
            this.function = () =>
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

        [Given("I have a null operation")]
        public void GivenIHaveANullOperation()
        {
            this.function = null;
        }

        [When(@"I invoke the operation with Retriable type ([^\s]*) passing no additional arguments")]
        public async Task WhenIInvokeTheOperationWithRetriableTypeActionPassingNoAdditionalArguments(RetryActionType actionType)
        {
            Action? action = null;
            Func<Task>? asyncAction = null;
            Func<Task<object>>? asyncFunction = null;
            if (this.function != null)
            {
                action = () => this.function();
                asyncAction = () =>
                {
                    action();
                    return Task.CompletedTask;
                };
                asyncFunction = () => Task.FromResult(this.function());
            }

            // This code is used when testing the case where different arguments are null, therefore the null forgiving operator is intentionally used
            // to accurately describe the intention of the test.
            switch (actionType)
            {
                case RetryActionType.Action:
                    Retriable.Retry(action!);
                    break;
                case RetryActionType.Function:
                    this.retryResult = Retriable.Retry(this.function!);
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

        [When("I attempt to invoke the action with Retriable type (.*) passing no additional arguments")]
        public async Task WhenIAttemptToInvokeTheActionWithRetriableTypePassingNoAdditionalArgumentsAsync(RetryActionType actionType)
        {
            try
            {
                await this.WhenIInvokeTheOperationWithRetriableTypeActionPassingNoAdditionalArguments(actionType).ConfigureAwait(false);
            }
            catch (Exception x)
            {
                this.exceptionThrownByRetry = x;
            }
        }

        [Then("the retriable operation should have been invoked (.*) time")]
        [Then("the retriable operation should have been invoked (.*) times")]
        public void ThenTheRetriableOperationShouldHaveBeenInvokedTime(int times)
        {
            Assert.AreEqual(times, this.callCount);
        }

        [Then(@"Retriable\.Retry should have thrown an ArgumentNullException")]
        public void ThenRetriable_RetryShouldHaveThrownAnArgumentNullException()
        {
            Assert.IsInstanceOf<ArgumentNullException>(this.exceptionThrownByRetry);
        }

        [Then(@"where a value is returned it should be the one returned by the final operation invocation \(action type (.*)\)")]
        public void ThenWhereAValueIsReturnedItShouldBeTheOneReturnedByTheFinalOperationInvocationActionTypeAction(RetryActionType actionType)
        {
            switch (actionType)
            {
                case RetryActionType.Function:
                case RetryActionType.AsyncFunction:
                    Assert.AreSame(this.lastObjectReturnedByFunction, this.retryResult);
                    break;
            }
        }
    }
}