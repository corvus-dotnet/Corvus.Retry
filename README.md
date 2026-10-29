# Corvus.Retry
[![Build Status](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml/badge.svg)](https://github.com/corvus-dotnet/Corvus.Retry/actions/workflows/build.yml)
[![GitHub license](https://img.shields.io/badge/License-Apache%202-blue.svg)](https://raw.githubusercontent.com/corvus-dotnet/Corvus.Retry/main/LICENSE)
[![IMM](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/total?cache=false)

This provides support for retriable and reliable/long running methods.

This package targets `net10.0`. If your project targets `netstandard2.0` or a different framework, use version 2.x.

## Features

There are three types of retry operation in this library:

### `RetryTask`
Starting a task to execute a function asynchronously, with retry
### `Retriable`
Wrapping a synchronous or asynchronous operation with retry semantics
### `ReliableTaskRunner`
Wrapping a long-running asynchronous operation in a host that ensures it continues to run even after a transient failure.

## Usage

### `Retriable`

This is the most common usage pattern. It wraps an existing method, and allows you to re-execute the method if it throws an exception.

```
Retriable.Retry(() => DoSomethingThatMightFail());
```

You can return values from your method

```
SomeResult result = Retriable.Retry(() => new SomeResult());
```

You can also use asynchronous methods

```
Task resultTask = Retriable.RetryAsync(() => SomeOperationAsync());
```

### Policy and Strategy

Two types help you to control when a failed operation is retried, and how that retry occurs. `IRetryPolicy` and `IRetryStrategy`.

#### `IRetryPolicy`

The retry policy gives you the ability to determine whether the operation should be retried or not, based on the exception that has been thrown.

This typically means you can distinguish between terminal exceptions (like bad input) and transient exceptions (like a network timeout).

There are three built-in retry policies:

##### `AnyExceptionPolicy`
This will always retry on any exception, and is the default for `Retriable`.

##### `DoNotRetryPolicy`
This will never retry, regardless of the exception. You use this to disable retry, without having to comment out your retry code.

Typically, you would do this with some kind of conditional

```
IRetryPolicy policy = isDebuggingOrWhatever ? new DoNotRetryPolicy() : new AnyExceptionPolicy();

var result = Retriable.Retry(() => DoSomething(), CancellationToken.None, new Count(10), policy);
```
##### `AggregatePolicy`
This gives you a means of ANDing together multiple policies. The `AggregatePolicy` only succeeds if ALL of its children succeed.

```
var aggregatePolicy =
  new AggregatePolicy
  {
    Policies =
    {
      new CustomPolicy1(),
      new CustomPolicy2(),
    },
  };
```

An `AggregatePolicy` with no child policies allows every retry, because none of its children objects.

##### Implementing a custom policy
It is very simple to create your own custom policy, and you will frequently do so. You implement the `IRetryPolicy` interface, and its `bool CanRetry(Exception exception);` method.

For example, let's imagine we were consuming an HttpService which occasionally gives us a `429 - Too Many Requests` error in an `HttpServiceException`.

We can implement a policy which will only retry if we receive this exception.

```
public class RetryOnTooManyRequestsPolicy : IRetryPolicy
{
  public bool CanRetry(Exception exception)
  {
    return (exception is HttpServiceException httpException && httpException.StatusCode == 429);
  }
}
```

#### `IRetryStrategy`
The `IRetryStrategy` controls the way in which the operation is retried. It controls both the _delay between each retry_ and the _number of times that it will be retried_.

There are several strategies provided out-of-the-box

##### `Count`
This tries the operation up to a specified number of times in total, with no delay between tries. For example, `new Count(10)` makes the first try and up to 9 retries. `Retriable` operations default to a `new Count(10)` strategy.

##### `DoNotRetry`
This is the strategy equivalent of the `DoNotRetryPolicy`. It forces a retry to be abandoned, regardless of policy.

##### `Linear`
This tries the operation up to a specified number of times in total, with a constant delay between each retry.

For example, `new Linear(TimeSpan.FromSeconds(1), 10)` will try up to 10 times: the first try and up to 9 retries. Each time it will delay by 1s. So the first retry will be after 1s (wall clock time 1s), the second after another 1s (wall clock time 2s), the third after another 1s (wall clock time 3s).

##### `Incremental`
This tries the operation up to a specified number of times in total, with an arithmetically increasing delay between each retry.

For example, `new Incremental(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))` will try up to 10 times: the first try and up to 9 retries. Each time it will increase the delay by 1s. So the first retry will be after 1s (wall clock time 1s), the second after another 2s (wall clock time 3s), the third after another 3s (wall clock time 6s).

This allows a slowly increasing delay.

##### `Backoff`
This tries the operation up to a specified number of times in total, with a delay that increases geometrically between each retry.

For example, `new Backoff(10, TimeSpan.FromSeconds(1))` will try up to 10 times: the first try and up to 9 retries. Each time it will increase the delay by a value calculated roughly like this: `2^n * (delta +/- a small random fudge)`.

This allows a rapidly increasing delay, with a bit of random jitter added to avoid contention.

You can also set a `MinBackoff` and `MaxBackoff` to limit the lower and upper bounds of the delay.

##### Implementing a custom strategy
It is slightly more complex to implement a custom retry strategy than it was to implement a retry policy. Although you can directly implement the `IRetryStrategy` interface, it is usually simpler to derive from the abstract `RetryStrategy` class.

In that case, you need to override the `TimeSpan PrepareToRetry(Exception lastException)` method. You can examine the exception and use your internal state to determine two things:

1) Should we record this exception, or filter it out? Typically, we will want to record the exception, and, if so, we call `this.AddException(lastException)`.
2) For how long should we delay before retrying? We return a `TimeSpan` from the method to determine this.

So, for example, `Count` implements the method like this:

```
public override TimeSpan PrepareToRetry(Exception lastException)
{
  if (lastException is null)
  {
    throw new ArgumentNullException(nameof(lastException));
  }

  this.AddException(lastException);
  this.tryCount++;
  return TimeSpan.Zero;
}
```

It updates its internal state to keep a count of the number of retries, adds the exception to the list of exceptions we have seen, and tells the strategy not to delay before retrying.

We also override the `bool CanRetry { get; }` property to determine whether this strategy still allows us to retry.

The `Count` strategy, for example, simply uses its internal state to determine whether to continue.

```
/// <inheritdoc/>
public override bool CanRetry
{
  get
  {
    return this.tryCount < this.maxTries;
  }
}
```

#### `ISleepService`
`Retriable` uses an implementation of an `ISleepService` to delay between retries. The sleep service offers synchronous `Sleep(TimeSpan)` and asynchronous `SleepAsync(TimeSpan)` methods to achieve a delay.

The default `SleepService` implementation uses `Thread.Sleep()` for the synchronous delay, and `Task.Delay()` for the asynchronous implementation.

The sleep service is internal to the library, and is replaced only by this project's own tests (for example, to remove the delay entirely). See [ADR 0001](Documentation/adr/0001-centralised-access-to-isleepservice.md).

### `RetryTask`

The `RetryTask` is an analog of `Task` which has built-in retry semantics.

Instead of calling `Task.Factory.StartNew()` you can call `RetryTask.Factory.StartNew()` with all the familiar parameters.

In addition to those usual parameters, you can pass an `IRetryPolicy` and an `IRetryStrategy` to control the retry behaviour. If you do not pass them, the defaults are `new Count()` (5 tries) and `AnyExceptionPolicy`. These differ from the `Retriable` default strategy, which is `new Count(10)`.

`RetryTask` uses the same sleep service as `Retriable` to delay between retries.

The overloads that take a state object use the same nullable annotations as `Task.Factory.StartNew()`: the state is `object?`, and the action or function takes an `object?` argument. Version 1 declared these as non-nullable `object`. If your project enables nullable reference types, a method or delegate that takes `object` must now take `object?`, or check for `null` before it uses the state:

```csharp
RetryTask.Factory.StartNew(state => Process((string)state!), "input", new Count(5), new AnyExceptionPolicy());
```

### `ReliableTaskRunner`
This is used to run a service-like operation that is supposed to execute 'forever' (or, at least, until cancellation). If the method fails, it should be re-started.

You start a task using the static `ReliableTaskRunner.Run()` method. For example

```
ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => DoSomeOperationAsync(cancellationToken));
```

When you want to terminate the task, you call the `StopAsync()` method e.g.

```
await runner.StopAsync();
```

As with the other retry methods, there are overloads where you can pass an `IRetryPolicy` to control the restart behaviour.

When a run fails, the runner asks the policy about each exception it threw (not the `AggregateException` around them), and restarts the operation only if the policy accepts every one. A run that cancels itself, for example because of an `HttpClient` timeout, counts as a failure: the policy is asked about the `OperationCanceledException`. A run that completes successfully, or that ends because you called `StopAsync()`, is not restarted. `StopAsync()` returns the task for the current run, so awaiting it shows how the last run ended.

## Samples

Runnable walkthroughs live in `Documentation/Examples` as .NET 10 file-based apps. Each one references the
library source via `#:project`, so they always run against the code in this repository. From the repo root:

    dotnet run Documentation/Examples/RetriableSample.cs
    dotnet run Documentation/Examples/ReliableTaskRunnerSample.cs
    dotnet run Documentation/Examples/RetryTaskSample.cs

The .NET 10 SDK is required. `RetriableSample` pings `endjin.com`, so it needs network access. The samples
deliberately wait between retries: `RetriableSample` takes about 2 minutes, the others about 20 seconds each.

## Benchmarks

`Solutions/Corvus.Retry.Benchmarks` uses [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure the overhead the
library adds. Every public type and member is benchmarked:

| Class | Public API it covers | Baseline |
|-------|----------------------|----------|
| `RetriableOverloadBenchmarks` | Every `Retriable.Retry` and `RetryAsync` overload, when the first try succeeds | `Retry(func)` and `RetryAsync(func)` |
| `RetriableBenchmarks` | `Retry` and `RetryAsync` with 0 and 2 failures before success | A hand-written retry loop |
| `RetryTaskFactoryOverloadBenchmarks` | Every `StartNew` overload of `RetryTask.Factory` and `RetryTask<T>.Factory` (29 each), when the first try succeeds | |
| `RetryTaskBenchmarks` | `RetryTask<T>.Factory.StartNew` with 0 and 2 failures | `Task<T>.Factory.StartNew` running a hand-written loop |
| `ReliableTaskRunnerBenchmarks` | `ReliableTaskRunner.Run` (both overloads) and `StopAsync`, including restarts after 1 and 4 failures | `Run` then `StopAsync` |
| `StrategyBenchmarks` | `PrepareToRetry` and `CanRetry` of `Count`, `DoNotRetry`, `Linear`, `Incremental` and `Backoff` (including its `MinBackoff`, `MaxBackoff`, `DeltaBackoff` and default bounds) for 1 and 9 failures | `Count` |
| `RetryStrategyBenchmarks` | `RetryStrategy.CanRetry`, `Exception`, `OnRetrying` and the `Retrying` event, which reads `RetryEventArgs` | `CanRetry` |
| `PolicyBenchmarks` | `AnyExceptionPolicy` and `DoNotRetryPolicy` `CanRetry` | `AnyExceptionPolicy` |
| `AggregatePolicyBenchmarks` | `AggregatePolicy.CanRetry` with 1, 4 and 16 child policies | A single policy |
| `SleepBenchmarks` | The default `ISleepService` (`Sleep` and `SleepAsync`), through a retry with a delay | The same retry with no delay |
| `ConstructionBenchmarks` | Every public constructor: strategies, policies, `RetryEventArgs` and both factories | `new Count()`, `new AnyExceptionPolicy()`, `new RetryEventArgs(...)` |

No benchmark waits for time to pass. Strategies calculate delays that are never waited for, and `SleepBenchmarks` asks
for a delay of one tick, which the sleep service rounds down to no wait. The `StartNew` overloads are called through
the test suite's `RetryTaskFactoryDriver`, so a test fails if an overload is added that the benchmarks would miss.

Run them in Release from the repo root:

    dotnet run -c Release --project Solutions/Corvus.Retry.Benchmarks -- --filter "*"

A full run has about 140 cases and takes the best part of an hour. Use `--filter` to pick benchmarks (for example
`"*Retriable*"`), and `--job short` for a quick exploratory run. Results are written to `BenchmarkDotNet.Artifacts`.
Timings vary between sessions, so compare ratios from the same run rather than absolute times from different runs.
The build compiles the benchmarks but does not run them.

## Building

The build uses [ZeroFailed](https://github.com/zerofailed/ZeroFailed) and requires PowerShell 7.4 or later and the .NET 10 SDK.
From the repo root, run:

    ./build.ps1

This restores, builds, tests (on Microsoft.Testing.Platform, with code coverage) and packages the solution
`Solutions/Corvus.Retry.slnx`. The build configuration lives in `.zf/config.ps1`. CI runs the same build in
GitHub Actions (`.github/workflows/build.yml`).

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the changes in each release, including the behaviour changes in 2.0.

## Licenses

[![GitHub license](https://img.shields.io/badge/License-Apache%202-blue.svg)](https://raw.githubusercontent.com/corvus-dotnet/Corvus.Retry/main/LICENSE)

Corvus.Retry is available under the Apache 2.0 open source license.

For any licensing questions, please email [&#108;&#105;&#99;&#101;&#110;&#115;&#105;&#110;&#103;&#64;&#101;&#110;&#100;&#106;&#105;&#110;&#46;&#99;&#111;&#109;](&#109;&#97;&#105;&#108;&#116;&#111;&#58;&#108;&#105;&#99;&#101;&#110;&#115;&#105;&#110;&#103;&#64;&#101;&#110;&#100;&#106;&#105;&#110;&#46;&#99;&#111;&#109;)

## Project Sponsor

This project is sponsored by [endjin](https://endjin.com), a UK based Technology Consultancy which specializes in Data & Analytics, AI & Cloud Native App Dev.

> We help small teams achieve big things.

We produce three free weekly newsletters: 

 - [Azure Weekly](https://azureweekly.info) for all things about the Microsoft Azure Platform
 - [Power BI Weekly](https://powerbiweekly.info) for all things Power BI
 - [Fabric Weekly](https://fabricweekly.info) for all things Microsoft Fabric

Keep up with everything that's going on at endjin via our [blog](https://endjin.com/blog), follow us on [Twitter](https://twitter.com/endjin), [YouTube](https://www.youtube.com/c/endjin) or [LinkedIn](https://www.linkedin.com/company/endjin).

We have become the maintainers of a number of popular .NET Open Source Projects:

- [Reactive Extensions for .NET](https://github.com/dotnet/reactive)
- [Reaqtor](https://github.com/reaqtive)
- [Argotic Syndication Framework](https://github.com/argotic-syndication-framework/)

And we have over 50 Open Source projects of our own, spread across the following GitHub Orgs:

- [endjin](https://github.com/endjin/)
- [Corvus](https://github.com/corvus-dotnet)
- [Menes](https://github.com/menes-dotnet)
- [Marain](https://github.com/marain-dotnet)
- [AIS.NET](https://github.com/ais-dotnet)

And the DevOps tooling we have created for managing all these projects is available on the [PowerShell Gallery](https://www.powershellgallery.com/profiles/endjin).

For more information about our products and services, or for commercial support of this project, please [contact us](https://endjin.com/contact-us). 

## Code of conduct

This project has adopted a code of conduct adapted from the [Contributor Covenant](http://contributor-covenant.org/) to clarify expected behavior in our community. This code of conduct has been [adopted by many other projects](http://contributor-covenant.org/adopters/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [&#104;&#101;&#108;&#108;&#111;&#064;&#101;&#110;&#100;&#106;&#105;&#110;&#046;&#099;&#111;&#109;](&#109;&#097;&#105;&#108;&#116;&#111;:&#104;&#101;&#108;&#108;&#111;&#064;&#101;&#110;&#100;&#106;&#105;&#110;&#046;&#099;&#111;&#109;) with any additional questions or comments.

## IP Maturity Matrix (IMM)

The IMM is endjin's IP quality framework.

[![Shared Engineering Standards](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/74e29f9b-6dca-4161-8fdd-b468a1eb185d?nocache=true)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/74e29f9b-6dca-4161-8fdd-b468a1eb185d?cache=false)

[![Coding Standards](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f6f6490f-9493-4dc3-a674-15584fa951d8?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f6f6490f-9493-4dc3-a674-15584fa951d8?cache=false)

[![Executable Specifications](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/bb49fb94-6ab5-40c3-a6da-dfd2e9bc4b00?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/bb49fb94-6ab5-40c3-a6da-dfd2e9bc4b00?cache=false)

[![Code Coverage](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/0449cadc-0078-4094-b019-520d75cc6cbb?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/0449cadc-0078-4094-b019-520d75cc6cbb?cache=false)

[![Benchmarks](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/64ed80dc-d354-45a9-9a56-c32437306afa?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/64ed80dc-d354-45a9-9a56-c32437306afa?cache=false)

[![Reference Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/2a7fc206-d578-41b0-85f6-a28b6b0fec5f?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/2a7fc206-d578-41b0-85f6-a28b6b0fec5f?cache=false)

[![Design & Implementation Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f026d5a2-ce1a-4e04-af15-5a35792b164b?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/f026d5a2-ce1a-4e04-af15-5a35792b164b?cache=false)

[![How-to Documentation](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/145f2e3d-bb05-4ced-989b-7fb218fc6705?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/145f2e3d-bb05-4ced-989b-7fb218fc6705?cache=false)

[![Date of Last IP Review](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/da4ed776-0365-4d8a-a297-c4e91a14d646?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/da4ed776-0365-4d8a-a297-c4e91a14d646?cache=false)

[![Framework Version](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/6c0402b3-f0e3-4bd7-83fe-04bb6dca7924?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/6c0402b3-f0e3-4bd7-83fe-04bb6dca7924?cache=false)

[![Associated Work Items](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/79b8ff50-7378-4f29-b07c-bcd80746bfd4?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/79b8ff50-7378-4f29-b07c-bcd80746bfd4?cache=false)

[![Source Code Availability](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/30e1b40b-b27d-4631-b38d-3172426593ca?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/30e1b40b-b27d-4631-b38d-3172426593ca?cache=false)

[![License](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/d96b5bdc-62c7-47b6-bcc4-de31127c08b7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/d96b5bdc-62c7-47b6-bcc4-de31127c08b7?cache=false)

[![Production Use](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/87ee2c3e-b17a-4939-b969-2c9c034d05d7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/87ee2c3e-b17a-4939-b969-2c9c034d05d7?cache=false)

[![Insights](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/71a02488-2dc9-4d25-94fa-8c2346169f8b?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/71a02488-2dc9-4d25-94fa-8c2346169f8b?cache=false)

[![Packaging](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/547fd9f5-9caf-449f-82d9-4fba9e7ce13a?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/547fd9f5-9caf-449f-82d9-4fba9e7ce13a?cache=false)

[![Deployment](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/edea4593-d2dd-485b-bc1b-aaaf18f098f9?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/edea4593-d2dd-485b-bc1b-aaaf18f098f9?cache=false)


[![OpenChain](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/66efac1a-662c-40cf-b4ec-8b34c29e9fd7?cache=false)](https://imm.endjin.com/api/imm/github/corvus-dotnet/Corvus.Retry/rule/66efac1a-662c-40cf-b4ec-8b34c29e9fd7?cache=false)

