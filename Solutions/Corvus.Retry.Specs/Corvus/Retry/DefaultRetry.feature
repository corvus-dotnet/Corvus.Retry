Feature: DefaultRetry
	In order to retry operations with a minimum of ceremony
	As a developer
	I want to get sensible defaults when I run a retriable operation with no additional arguments

Scenario Outline: ActionSucceedsFirstTime
	Given I have an operation that succeeds first time
	When I invoke the operation with Retriable type <RetryType> passing no additional arguments
	Then the retriable operation should have been invoked 1 time

	Examples:
	| RetryType                              |
	| Action                                 |
	| Function                               |
	| AsyncAction                            |
	| AsyncActionSpecifyingContinueContext   |
	| AsyncFunction                          |
	| AsyncFunctionSpecifyingContinueContext |

Scenario Outline: ActionSucceedsAfterFailures
	Given I have an operation that fails 3 times then succeeds
	When I invoke the operation with Retriable type <RetryType> passing no additional arguments
	Then the retriable operation should have been invoked 4 times
	And where a value is returned it should be the one returned by the final operation invocation (action type <RetryType>)

	Examples:
	| RetryType                              |
	| Action                                 |
	| Function                               |
	| AsyncAction                            |
	| AsyncActionSpecifyingContinueContext   |
	| AsyncFunction                          |
	| AsyncFunctionSpecifyingContinueContext |

Scenario: ValidateOperationArgument
    Given I have a null operation
	When I attempt to invoke the action with Retriable type <RetryType> passing no additional arguments
    Then Retriable.Retry should have thrown an ArgumentNullException

	Examples:
	| RetryType                              |
	| Action                                 |
	| Function                               |
	| AsyncAction                            |
	| AsyncActionSpecifyingContinueContext   |
	| AsyncFunction                          |
	| AsyncFunctionSpecifyingContinueContext |