// <copyright file="PolicyStepBindings.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using NUnit.Framework;
    using Reqnroll;

    [Binding]
    public class PolicyStepBindings
    {
        private bool canRetryResult;
        private Exception? exceptionThrownByCanRetry;

        public IRetryPolicy? Policy { get; set; }

        [Given("I have a DoNotRetryPolicy")]
        public void GivenIHaveADoNotRetryPolicy()
        {
            this.Policy = new DoNotRetryPolicy();
        }

        [Given("I have an AnyExceptionPolicy")]
        public void GivenIHaveAnAnyExceptionPolicy()
        {
            this.Policy = new AnyExceptionPolicy();
        }

        [When("I ask if I can retry with an exception of type '(.*)'")]
        public void WhenIAskIfICanRetryWithAnExceptionOfTypeInvalidOperationException(string exceptionType)
        {
            Exception exception = exceptionType switch
            {
                "InvalidOperationException" => new InvalidOperationException(),
                "ArgumentNullException"     => new ArgumentNullException(),
                "NullReferenceException"    => new NullReferenceException(),
                _ => throw new ArgumentException($"Unknown exception type {exceptionType}", nameof(exceptionType)),
            };

            this.canRetryResult = this.Policy!.CanRetry(exception);
        }

        [When("I ask if I can retry with a null exception argument")]
        public void WhenIAskIfICanRetryWithANullExceptionArgument()
        {
            try
            {
                this.Policy!.CanRetry(null!);
            }
            catch (Exception x)
            {
                this.exceptionThrownByCanRetry = x;
            }
        }

        [Then("CanRetry should return (true|false)")]
        public void ThenCanRetryShouldReturnFalse(bool expectedResult)
        {
            Assert.AreEqual(expectedResult, this.canRetryResult);
        }

        [Then("CanRetry should have thrown an ArgumentNullException")]
        public void ThenCanRetryShouldHaveThrownAnArgumentNullException()
        {
            Assert.IsInstanceOf<ArgumentNullException>(this.exceptionThrownByCanRetry);
        }
    }
}