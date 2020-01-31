Feature: DoNotRetryPolicy
    In order to make retries optional
    As a developer
    I want to be able to supply a policy that will not attempt retries

Scenario: Test for retry
    Given I have a DoNotRetryPolicy
    When I ask if I can retry with an exception of type 'InvalidOperationException'
    Then CanRetry should return false

Scenario: Validate arguments
    Given I have a DoNotRetryPolicy
    When I ask if I can retry with a null exception argument
    Then CanRetry should have thrown an ArgumentNullException
