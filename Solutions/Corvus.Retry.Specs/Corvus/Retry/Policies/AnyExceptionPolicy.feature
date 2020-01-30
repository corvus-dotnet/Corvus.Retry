Feature: AnyExceptionPolicy
    In order to be robust in the face of failures
    As a developer
    I want to be able to supply a policy that will attempt retries in the event of any exception

Scenario Outline: Test for retry
    Given I have an AnyExceptionPolicy
    When I ask if I can retry with an exception of type '<exceptionType>'
    Then CanRetry should return true

    Examples:
    | exceptionType             |
    | InvalidOperationException |
    | ArgumentNullException     |
    | NullReferenceException    |

Scenario: Validate arguments
    Given I have an AnyExceptionPolicy
    When I ask if I can retry with a null exception argument
    Then CanRetry should have thrown an ArgumentNullException