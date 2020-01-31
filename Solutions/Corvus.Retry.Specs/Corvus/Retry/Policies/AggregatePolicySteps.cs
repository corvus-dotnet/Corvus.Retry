// <copyright file="AggregatePolicySteps.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Moq;
    using TechTalk.SpecFlow;

    [Binding]
    public class AggregatePolicySteps
    {
        private readonly PolicyStepBindings policyBindings;

        public AggregatePolicySteps(PolicyStepBindings policyBindings)
        {
            this.policyBindings = policyBindings;
        }

        private AggregatePolicy Policy
        {
            get => (AggregatePolicy)this.policyBindings.Policy;
            set => this.policyBindings.Policy = value;
        }

        [Given("I have an AggregatePolicy")]
        public void GivenIHaveAnAggregatePolicy()
        {
            this.Policy = new AggregatePolicy();
        }

        [Given("all the aggregated policies say not to retry")]
        public void GivenAllTheAggregatedPoliciesSayNotToRetry()
        {
            this.Policy.Policies.AddRange(MakePolicies(10, false));
        }

        [Given("all but one of the aggregated policies say not to retry")]
        public void GivenAllButOneOfTheAggregatedPoliciesSayNotToRetry()
        {
            this.Policy.Policies.AddRange(MakePolicies(10, true));
            this.Policy.Policies.AddRange(MakePolicies(1, false));
        }

        [Given("all the aggregated policies say to retry")]
        public void GivenAllTheAggregatedPoliciesSayNotRetry()
        {
            this.Policy.Policies.AddRange(MakePolicies(10, true));
        }

        private static IEnumerable<IRetryPolicy> MakePolicies(int count, bool result)
        {
            return Enumerable
                .Range(0, count)
                .Select(_ => MakePolicy(result));
        }

        private static IRetryPolicy MakePolicy(bool result)
        {
            var m = new Mock<IRetryPolicy>();
            m.Setup(p => p.CanRetry(It.IsAny<Exception>())).Returns(result);
            return m.Object;
        }
    }
}
