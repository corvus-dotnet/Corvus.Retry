# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

These changes are planned for 3.0.0.

### Added

### Changed

### Removed

## [2.0.0]

### Added

- A `net10.0` target, alongside `netstandard2.0`.
- A software bill of materials (SBOM) in Covenant, SPDX and CycloneDX formats, attached to each GitHub Release with
  the NuGet packages.
- Benchmarks for every public API, in `Solutions/Corvus.Retry.Benchmarks`.
- Samples as .NET 10 file-based apps, replacing the Polyglot notebooks.

### Changed

- `RetryTask` runs every retry on the caller's scheduler. Before, only the first try did; retry decisions and delays
  run on the thread pool.
- With `TaskCreationOptions.AttachedToParent`, `RetryTask` attaches the returned task to the parent, not each try.
  Tries that were later retried no longer fault the parent.
- `RetryTask` and `ReliableTaskRunner` ask the policy about the exceptions the operation threw, not the
  `AggregateException` around them. The policy is called once for each exception, depth first, and every one must be
  accepted for a retry or restart. An `AggregateException` with no inner exceptions is passed to the policy itself.
  A custom policy that tests for `AggregateException`, or counts how often it is called, needs updating.
- `RetryTask` reports cancellation as a `Canceled` task that carries the caller's token. In 1.x, cancelling during
  retries gave a task that completed successfully (for an action) or faulted (for a function).
- `RetryTask.Factory.StartNew` and `RetryTask<T>.Factory.StartNew` return a `Canceled` task when the token is
  already cancelled. In 1.x, the non-generic factory returned a task that completed successfully without running the
  action.
- When the policy has already refused a retry, `RetryTask` reports the failure even if the token is then cancelled.
- `ReliableTaskRunner` asks the policy what to do when a run cancels itself (for example an `HttpClient` timeout).
  With the default policy the run is restarted. In 1.x, the runner stopped silently.
- Null arguments throw `ArgumentNullException`. In 1.x, some overloads threw `NullReferenceException`.
- The 5-argument `Retriable.RetryAsync` overloads throw `ArgumentNullException` when called with a null argument,
  instead of returning a faulted task. Code that awaits the call directly sees no difference.
- An `AggregatePolicy` with no child policies allows a retry.
- The `state` parameters of the `RetryTask` factory overloads are annotated as `object?` (`Action<object?>` and
  `Func<object?, T>`), matching `TaskFactory.StartNew`.
- Lower time and allocation costs:
  - `Retriable.Retry` and `Retriable.RetryAsync` allocate less on every call.
  - Policy checks no longer allocate.
  - Strategies create their exception list on the first failure.
  - `Backoff` shares one random number generator instead of creating one per delay.
  - `RetryTask` retries allocate about 25% less.

### Fixed

- `RetryTask` lost the failure when the operation gave up. The returned task now faults with every failure the strategy
  recorded, or with the last try's exception if the strategy recorded none (for example with `DoNotRetry`). In 1.x,
  the task faulted with an empty `AggregateException`.
- `RetryTask` kept retrying after cancellation. It now stops, ends `Canceled` with the caller's token, stops waiting
  for the strategy's delay, and does not raise `Retrying` for a retry that will not run.
- `ReliableTaskRunner.StopAsync` during a restart could return the previous, faulted run while a new run kept going.
- `Retriable.RetryAsync` reset the stack trace of the exception it rethrew when it gave up.
- `RetryTask.Factory` and `RetryTask<T>.Factory` could give different instances to threads that accessed them for the
  first time at once.
- `AggregatePolicy.CanRetry` threw `NullReferenceException` if `Policies` had never been accessed.
- `Backoff` gave concurrent retries on .NET Framework the same jitter, because each delay created a `Random` with a
  time-based seed.

## [1.0.7] - 2023-05-03

### Changed

- Clarified the documentation of the `maxTries` parameter.
- Added Polyglot notebook samples for `Retriable`, `RetryTask` and `ReliableTaskRunner`.
- Updated dependencies.

## [1.0.6] - 2022-01-31

### Changed

- Updated to Endjin.RecommendedPractices 2.

## [1.0.5] - 2021-12-10

### Changed

- Enabled NuGet lock files.
- Updated dependencies.

## [1.0.4] - 2021-04-28

### Changed

- Updated dependencies.

## [1.0.3] - 2021-04-09

### Changed

- Updated dependencies.

## [1.0.2] - 2021-03-16

### Changed

- Updated dependencies.

## [1.0.1] - 2021-02-11

### Changed

- Updated dependencies.

## [1.0.0] - 2020-07-13

### Changed

- First stable release. The API is unchanged from 0.5.0.

## [0.5.0] - 2020-07-09

### Added

- Nullable reference type annotations.
- Unit tests.

### Changed

- Updated to SpecFlow 3.3 and Endjin.RecommendedPractices.

## [0.4.0] - 2019-09-06

### Changed

- Updated dependencies.

## [0.3.0] - 2019-09-06

### Added

- Basic argument validation.

## [0.2.0] - 2019-08-21

### Changed

- Build and release process changes only.

## [0.1.1] - 2019-08-21

### Changed

- Build and release process changes only.

## [0.1.0] - 2019-08-21

### Added

- First release: `Retriable`, `RetryTask`, `ReliableTaskRunner`, retry strategies and retry policies.

[Unreleased]: https://github.com/corvus-dotnet/Corvus.Retry/compare/2.0.0...HEAD
[2.0.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.7...2.0.0
[1.0.7]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.6...1.0.7
[1.0.6]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.5...1.0.6
[1.0.5]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.4...1.0.5
[1.0.4]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.3...1.0.4
[1.0.3]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.2...1.0.3
[1.0.2]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.1...1.0.2
[1.0.1]: https://github.com/corvus-dotnet/Corvus.Retry/compare/1.0.0...1.0.1
[1.0.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.5.0...1.0.0
[0.5.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.4.0...0.5.0
[0.4.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.3.0...0.4.0
[0.3.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.2.0...0.3.0
[0.2.0]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.1.1...0.2.0
[0.1.1]: https://github.com/corvus-dotnet/Corvus.Retry/compare/0.1.0...0.1.1
[0.1.0]: https://github.com/corvus-dotnet/Corvus.Retry/releases/tag/0.1.0
