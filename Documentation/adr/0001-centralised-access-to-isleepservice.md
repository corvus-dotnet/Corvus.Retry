# Centralised access to ISleepService

## Status

Proposed

## Context

When implementing nullability we found that the code around `ISleepService` did not fit well with nullable references. There were static properties which never returned `null` but could usefully be set to `null` at the end of a unit test (to revert to the default behaviour).

We began to update the implementation so that the intent of the property was clearer (using methods for setting and unsetting the property) but found that there were many places within the codebase which had their own versions of the `ISleepService`. 

## Decision

We centralised the access to `ISleepService` by implementing a static class `SleepService` which provided access to an instance of `ISleepService` which defaulted to the non-test behaviour but can be set to override that behaviour.

We made this new behaviour `internal` because it's purely for testing purposes and we allowed the `Corvus.Retry.Specs` project to access internals within `Corvus.Retry`.

## Consequences

- The `ISleepService` handling now fits well with C#8 nullable references
- Library features which are purely there to enable unit testing are now internal
- There is less duplication of code
- This is a breaking change from `Endjin.Retry` but nothing outside of the project should be using this code (as it's entirely for testing purposes)