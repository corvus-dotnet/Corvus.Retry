Feature: AggregatePolicy
    In order to be able to refine my reasons for retrying
    As a developer
    I want to be able to ensure that retry is only attempted if multiple policies are satisfied

Scenario: All policies say no
    Given I have an AggregatePolicy
    And all the aggregated policies say not to retry
    When I ask if I can retry with an exception of type 'InvalidOperationException'
    Then CanRetry should return false

Scenario: All policies but one say no
    Given I have an AggregatePolicy
    And all but one of the aggregated policies say not to retry
    When I ask if I can retry with an exception of type 'InvalidOperationException'
    Then CanRetry should return false

Scenario: All policies say yes
    Given I have an AggregatePolicy
    And all the aggregated policies say to retry
    When I ask if I can retry with an exception of type 'InvalidOperationException'
    Then CanRetry should return true

Scenario: Validate arguments
    Given I have an AggregatePolicy
    When I ask if I can retry with a null exception argument
    Then CanRetry should have thrown an ArgumentNullException