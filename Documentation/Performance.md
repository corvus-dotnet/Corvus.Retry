# Performance of Corvus.Retry 3.0

This document compares the time and the memory of Corvus.Retry 3.0 with version 2.0. It also gives the cost of the
functions that are new in 3.0, and the changes that make 3.0 slower than 2.0 in some cases.

The benchmarks are in `Solutions/Corvus.Retry.Benchmarks`. The [README](../README.md#benchmarks) tells you how to run
them.

## Summary

- Strategies do not allocate memory for a call. In 2.x, each call needed a new strategy.
- `Retriable.Retry` and `Retriable.RetryAsync` allocate less memory than in 2.0. When the operation succeeds at the
  first attempt, the library allocates no memory for `Retry`. For `RetryAsync`, the only allocation is the returned
  `Task<T>`, which a hand-written `async` retry loop also allocates.
- An asynchronous `Retriable` call that completes immediately takes 3 ns to 22 ns more than in 2.0.
- `RetryTask` with failures allocates approximately 900 B more than in 2.0, and `ReliableTaskRunner.StopAsync` is
  slower, because of changes to their behaviour.
- A `RetryTaskFactory` with no settings adds approximately 10 ns to a `RunValueTask` call, and allocates no memory.
  `Run` allocates the `Task<T>` that it returns (72 B) and nothing else.
- A hedged attempt after the first attempt starts on the thread pool. In the benchmark, this adds approximately 620 B
  and 2.4 µs to a hedged call.
- The change from `ReliabilityPipeline` to the configured factories (see [The API reshape](#the-api-reshape)) did not
  change the time or the memory of the `ValueTask` path, the circuit breaker, the fallback, `Retriable` or `StartNew`.
  A hedged `Run` allocates 150 B to 320 B more than the hedged pipeline did.

## Test environment

| Item | Value |
|---|---|
| Hardware | Intel Core i7-13700H laptop (6 performance cores and 8 efficiency cores, 20 logical processors), High performance power plan |
| Operating system | Windows 11 10.0.26300 |
| .NET | SDK 10.0.403, runtime 10.0.14, X64 RyuJIT x86-64-v3 |
| BenchmarkDotNet | 0.15.8, with `[MemoryDiagnoser]` on all the benchmark classes |
| Date | October 2026. The API reshape runs: 31 October 2026 |
| Version 2.0 | Commit `03c4590` |
| Version 3.0 | Branch `features/v3`. Before the API reshape: commit `02dcd55`. After: commit `fa7c11d` |

## Method

1. We ran all the benchmarks with `--job short` on 2.0 (137 cases) and then on 3.0 (255 cases). No other builds or
   tests ran between the two runs.
2. We ran the benchmark classes for the hot paths again with the default job, one version after the other. These
   classes are `RetriableBenchmarks`, `RetriableOverloadBenchmarks`, `StrategyBenchmarks`, `PolicyBenchmarks`,
   `RetryTaskBenchmarks` and `ReliabilityPipelineBenchmarks` (now `RetryTaskFactoryBenchmarks`). The two versions
   used the same benchmark code.
3. After the changes that this document describes in [Changes from the analysis](#changes-from-the-analysis), we ran
   the benchmarks that these changes affect again. Each run used the same job as the run before the change.
4. After the changes in [Changes to the behaviour](#changes-to-the-behaviour), we ran the benchmarks that these
   changes affect with the short job, before and after the changes.
5. After [the API reshape](#the-api-reshape), we ran the benchmarks before the reshape (commit `02dcd55`) and after it,
   one after the other: `RetryTaskFactoryBenchmarks` and `CircuitBreakerBenchmarks` with the default job;
   `FallbackBenchmarks`, `HedgingBenchmarks`, `RetryTaskBenchmarks` and `RetriableBenchmarks` with the short job; and
   `ConstructionBenchmarks` after the reshape only, with the short job.

Some benchmarks have a different name in 3.0:

| 2.0 | 3.0 |
|---|---|
| `SleepBenchmarks` | `DelayBenchmarks` |
| `Retry_WithDelay_Sleep` | `Retry_WithDelay_ThreadSleep` |
| `RetryAsync_WithDelay_SleepAsync` | `RetryAsync_WithDelay_TaskDelay` |
| `RetryStrategyBenchmarks` | Removed, together with the `RetryStrategy` base class |

The API reshape renamed these benchmarks:

| Before the reshape | After the reshape |
|---|---|
| `ReliabilityPipelineBenchmarks` | `RetryTaskFactoryBenchmarks` |
| `Execute_*`, `ExecuteOfT_ResultRetry` | `RunValueTask_*`, `RunValueTaskOfT_ResultRetry`. The new cases `Run_NoOptions`, `Run_Unit`, `Run_WithFailures` and `RunOfT_ResultRetry` measure the `Task` path. |
| `ReliabilityPipelineOptions*` and `HedgingOptions` (construction) | `RetryTaskFactory*` and `HedgedTaskFactory*`, in the category `Settings` |
| `RetryPolicies_Handle*`, `RetryPolicies_HandleInner` | `RetryPolicies_For*`, `RetryPolicies_ForInner` |
| `BrokenCircuitException`, `IsolatedCircuitException`, `HedgingEventArgs`, `FallbackArgsOfT` | `CircuitRejectedException`, `CircuitIsolatedException`, `HedgedAttemptEventArgs`, `FallbackEventArgsOfT` |

### Accuracy

The memory values are exact. The time values are not exact. On the test computer, the times changed by a factor of 2
to 4 between runs, also for benchmarks that do not use the library. For example, the hand-written loop
`HandWrittenLoopAsync [Failures=2]` gave 3.61 µs, 14.35 µs and 6.18 µs in three runs. Thus, compare the ratios in one
run, not the absolute times from different runs. Times of less than 1 ns are less than the resolution of the timer.

## Version 2.0 and version 3.0

These results come from the default job, unless the table shows "short job". The notes R1 thru R3 refer to the
[causes of the slower results](#causes-of-the-slower-results).

| Benchmark | 2.0 | 3.0 | Note |
|---|---|---|---|
| `Retriable.Retry`, 0 failures | 6.5 ns, 104 B | 7.3 ns, 88 B | The benchmark allocates the 88 B. The library allocates 0 B. |
| `Retriable.Retry`, 2 failures | 3.81 µs, 688 B | 1.76 µs, 520 B | |
| `Retriable.RetryAsync`, 0 failures | 25.7 ns, 176 B | 23.0 ns, 160 B | The library allocates 72 B, the same as a hand-written loop. |
| `Retriable.RetryAsync`, 2 failures | 3.65 µs, 1,944 B | 5.15 µs, 1,776 B | The difference in time is in the range of the noise. |
| `RetryAsync_Action` | 6.2 ns, 40 B | 13.6 ns, 0 B | R1 |
| `RetryAsync_Action_Token_Strategy_Policy` | 4.0 ns, 0 B | 25.6 ns, 0 B | R1 |
| `RetryAsync_Function` | 14.0 ns, 112 B | 17.5 ns, 72 B | |
| `Retry_Function` | 2.9 ns, 40 B | 3.1 ns, 0 B | |
| `RetryTask`, 0 failures | 1.18 µs, 520 B | 1.17 µs, 536 B | |
| `RetryTask`, 2 failures | 10.62 µs, 2,979 B | 13.42 µs, 3,212 B | R2 |
| `Count` strategy, 9 failures | 95.3 ns, 368 B | 10.6 ns, 0 B | |
| `Linear` strategy, 9 failures | 99.9 ns, 376 B | 10.0 ns, 0 B | |
| `Backoff` strategy, 9 failures | 253 ns, 392 B | 205 ns, 0 B | |
| `ReliableTaskRunner.Run` and `StopAsync` (short job) | 384 ns, 832 B | 1.93 µs, 1.3 KB | R3 |
| `RetryAsync_WithDelay_TaskDelay` (short job) | 3.37 µs, 1,112 B | 2.01 µs, 992 B | |
| `Retry_WithDelay_ThreadSleep` (short job) | 3.82 µs, 448 B | 3.56 µs, 328 B | |
| `RetryEventArgs` constructor (short job) | 10.7 ns, 32 B | 0.3 ns, 0 B | |

## Functions that are new in 3.0

The last column gives a benchmark in the same class, for comparison. These are the figures from the runs after the API
reshape (31 October 2026). The `RetryTaskFactory` and circuit breaker rows come from the default job; the other rows
come from the short job.

| Benchmark | 3.0 | Comparison |
|---|---|---|
| `RetryTaskFactory.RunValueTask`, no settings | 13.9 ns, 0 B | Hand-written loop: 4.3 ns, 0 B |
| `RetryTaskFactory.Run<T>`, no settings | 17.5 ns, 72 B | The 72 B is the `Task<T>` that `Run` returns. |
| `RetryTaskFactory.Run`, no result, no settings | 14.8 ns, 0 B | `Run` returns `Task.CompletedTask` when the operation completes synchronously. |
| `RunValueTask`, `AttemptTimeout` | 60.5 ns, 144 B | |
| `RunValueTask`, `TotalTimeout` | 60.1 ns, 144 B | |
| `RunValueTask`, `AttemptTimeout` and `TotalTimeout` | 142 ns, 432 B | |
| `RunValueTask`, 2 failures | 1.90 µs, 432 B | Hand-written loop: 1.91 µs, 432 B |
| `Run`, 2 failures | 1.89 µs, 504 B | The 72 B more is the returned `Task<T>`. |
| `RunValueTask`, 2 failures, with `OnRetry` | 1.92 µs, 432 B | The callback allocates no memory. `RetryEventArgs` is a value type. |
| `RetryTaskFactory<T>.RunValueTask`, one result retry | 23.3 ns, 24 B | The 24 B is a boxed `int` in `RetryAttempt.Result`. |
| `RetryTaskFactory<T>.Run`, one result retry | 26.7 ns, 96 B | The boxed `int` and the returned `Task<T>`. |
| Circuit breaker, closed circuit, success | 75.0 ns, 0 B | No circuit breaker: 29.5 ns |
| Circuit breaker, closed circuit, failure | 9.04 µs, 1,480 B | No circuit breaker: 8.64 µs, 1,480 B |
| Circuit breaker, open or isolated circuit (the call is rejected) | 5.56 µs, 920 B | |
| Circuit breaker, half-open circuit, the test call fails | 9.19 µs, 1,480 B | |
| Fallback, set but not used (short job) | 53.1 ns, 24 B | No fallback: 28.5 ns, 0 B. The 24 B is the object that records the outcome of the attempts. |
| Fallback, used for a result (short job) | 53.5 ns, 24 B | No fallback: 27.2 ns, 0 B |
| Fallback, used for an exception (short job) | 9.34 µs, 1,504 B | No fallback: 9.50 µs, 1,480 B |
| Hedging, the first attempt succeeds (short job) | 209 ns, 704 B | `RetryTaskFactory<T>.Run`: 36.4 ns, 72 B |
| Hedging, the first attempt fails and the second succeeds (short job) | 15.2 µs, 1,594 B | `RetryTaskFactory<T>.Run`: 2.10 µs, 288 B. Attempt 2 starts on the thread pool. |
| Hedging, stagger of zero, both attempts complete (short job) | 9.57 µs, 1,836 B | |
| Hedging, stagger of zero, the attempt that loses is cancelled (short job) | 9.96 µs, 3,270 B | |
| `ReliableTaskRunner.Run` and `DisposeAsync` (short job) | 2.01 µs, 1.22 KB | Not run again after the reshape. |
| `RetryTask`, delay of one tick, 2 failures (short job) | 49.2 µs, 4,175 B | `Task<T>.Factory.StartNew` with a hand-written loop: 18.6 µs, 579 B |

Exceptions cause most of the cost of a failure. A rejection by the circuit breaker is an exception, thus it costs
approximately the same as a failure of the operation. The times of the short-job hedging rows have a large error (the
error of the first row with a failure is larger than its mean); compare their memory, not their time.

## The API reshape

The first draft of 3.0 had a `ReliabilityPipeline` with an options record. The release has configured
`RetryTaskFactory` and `HedgedTaskFactory` types instead (see
[ADR 0003](adr/0003-follow-taskfactory-not-a-pipeline.md)). The retry and hedging engines did not change. These runs
compare the two, one after the other on the same machine. The hand-written loop ran at 8.7 ns in the run before the
reshape and 4.3 ns in the run after it, thus the times of the two runs are not directly comparable, but the ratios to
the hand-written loop and the memory are.

| Benchmark | Before: `ReliabilityPipeline.ExecuteAsync` | After: `RunValueTask` | After: `Run` (Task) |
|---|---|---|---|
| No settings | 30.4 ns, 0 B (3.5 × the loop) | 13.9 ns, 0 B (3.2 × the loop) | 17.5 ns, 72 B (4.0 × the loop) |
| No result, no settings | 14.3 ns, 0 B | 15.7 ns, 0 B | 14.8 ns, 0 B |
| `AttemptTimeout` | 63.3 ns, 144 B | 60.5 ns, 144 B | |
| `TotalTimeout` | 60.0 ns, 144 B | 60.1 ns, 144 B | |
| Both timeouts | 206 ns, 432 B | 142 ns, 432 B | |
| `ContinueOnCapturedContext` | 23.8 ns, 0 B | 12.8 ns, 0 B | |
| 2 failures | 5.47 µs, 432 B (loop: 5.32 µs) | 1.90 µs, 432 B (loop: 1.91 µs) | 1.89 µs, 504 B |
| 2 failures, `OnRetry` | 6.80 µs, 432 B | 1.92 µs, 432 B | |
| One result retry | 53.0 ns, 24 B | 23.3 ns, 24 B | 26.7 ns, 96 B |

| Benchmark (short job unless stated) | Before the reshape | After the reshape |
|---|---|---|
| Circuit breaker, closed, success (default job) | 76.6 ns, 0 B (an earlier run; the run before the reshape was stopped) | 75.0 ns, 0 B |
| Circuit breaker, closed, failure (default job) | 9.3 µs, 1,480 B | 9.04 µs, 1,480 B |
| Circuit breaker, rejected (default job) | 5.6 µs, 920 B | 5.56 µs, 920 B |
| Fallback, set but not used | 52.2 ns, 24 B | 53.1 ns, 24 B |
| Fallback, used for a result | 74.7 ns, 24 B | 53.5 ns, 24 B |
| Fallback, used for an exception | 11.2 µs, 1,504 B | 9.34 µs, 1,504 B |
| Hedging baseline: a factory with retries, success | 42.3 ns, 0 B (`ExecuteAsync`) | 36.4 ns, 72 B (`Run`) |
| Hedging, the first attempt succeeds | 244 ns, 488 B | 209 ns, 704 B |
| Hedging, the first attempt fails and the second succeeds | 14.6 µs, 1,450 B | 15.2 µs, 1,594 B |
| Hedging, stagger of zero, both attempts complete | 7.84 µs, 1,518 B | 9.57 µs, 1,836 B |
| Hedging, stagger of zero, the loser is cancelled | 10.3 µs, 2,950 B | 9.96 µs, 3,270 B |
| `RetryTask.StartNew`, 0 failures | 4.59 µs, 536 B | 7.62 µs, 536 B |
| `RetryTask.StartNew`, 2 failures | 44.8 µs, 3,253 B | 41.2 µs, 3,925 B |
| `RetryTask.StartNew`, one-tick delay, 2 failures | 40.5 µs, 4,142 B | 49.2 µs, 4,175 B |
| `Retriable.Retry`, 2 failures | 6.57 µs, 432 B | 4.75 µs, 432 B |
| `Retriable.RetryAsync`, 2 failures | 7.27 µs, 1,688 B | 7.09 µs, 1,688 B |
| `Retriable.RetryAsync` with a token and `OnRetry`, 2 failures | 8.01 µs, 1,688 B | 7.44 µs, 1,688 B |

These are the results:

- **`RunValueTask` costs the same as `ExecuteAsync` did.** It uses the same engine and the same operation adapters. Every
  memory value is identical, and the ratios to the hand-written loop are the same within the noise.
- **`Run` adds the `Task<T>` that it returns**, 72 B, through `ValueTask<T>.AsTask()`. A `Run` without a result returns
  `Task.CompletedTask` when the operation completes synchronously, and allocates nothing. The result-checking adapter
  for `Task` operations (`ResultCheckedTaskOperation<T>`) is a struct, and adds nothing.
- **The circuit breaker, the fallback, `Retriable` and the strategies did not change.** Their memory is identical. Their
  times moved by less than the noise of the short job.
- **A hedged `Run` allocates 150 B to 320 B more than the hedged pipeline did.** On each call, the hedged factory makes
  the internal `HedgingOptions` record for the engine, a closure that adapts the `Task` operation to the `ValueTask`
  that the engine takes, and the returned `Task<T>`. When `OperationForAttempt` is set, the adapter for the generated
  operation adds a second closure. The hedged times moved by less than the noise. We accept this cost: hedging already
  costs approximately 1 KB when attempt 2 starts on the thread pool, and the `Task` shape keeps `OperationForAttempt`
  simple. See [Changes that we did not make](#changes-that-we-did-not-make).
- **The hedging baseline row allocates 72 B after the reshape**, because a hedged factory has only `Run`, thus the
  comparison factory also uses `Run`. It allocated 0 B when it used `ExecuteAsync`.
- **`StartNew` with 2 failures gave 3,253 B before and 3,925 B after.** The `StartNew` code path changed only in reading
  the strategy, the policy and the `TimeProvider` from the factory, which allocates nothing. The earlier analysis saw
  2,989 B to 3,882 B for this case on the same code: the allocation of a task that the scheduler runs with
  continuations changes between runs. The 0-failure case and the one-tick case allocated the same memory before and
  after.

## Causes of the slower results

We accept these results. Each one is the result of a change to the behaviour of 3.0.

- **R1: an asynchronous `Retriable` call that completes immediately takes 3 ns to 22 ns more.** It allocates the same
  memory or 40 B less. In 3.0, one retry loop does all the asynchronous calls. This loop also examines the token, the
  timeouts and the callbacks, thus its state machine is larger. If the operation completes asynchronously, this
  difference is not important: the `DelayBenchmarks` asynchronous cases are 40% faster in 3.0.
- **R2: `RetryTask` with failures allocates approximately 900 B more. The time is in the range of the noise.** In 3.0,
  a retry does not block a thread. The scheduler runs each retry as a continuation, and a delay is a task with a
  continuation. The state for each call is in the factory, not in the strategy. With the current benchmark code and
  the short job, one run after the other, 2.0 gave 24.0 µs and 2,989 B, and 3.0 gave 23.9 µs and 3,882 B. In the same
  runs, the time of the hand-written loop changed by a factor of 2, thus the two times are the same within the noise.
  The 3,212 B in the table came from the old benchmark code, as for the `RetriableBenchmarks` results. The
  `Exceptions` column shows 2 exceptions for each failure of `RetryTask`, and 1 for the hand-written loop. The runtime
  raises a second first-chance exception inside `Task.ExecuteWithThreadLocal` for each task whose delegate throws. The
  hand-written loop catches the exception inside its delegate, thus the runtime does not see it. 2.0 is the same.
- **R3: `ReliableTaskRunner.StopAsync` takes approximately 1.9 µs, and allocates approximately 500 B more.**
  `StopAsync` uses `CancellationTokenSource.CancelAsync`, which continues on a different thread. It also keeps all
  the exceptions of the operation. The runner also keeps a task for the restart decision. `StopAsync` and
  `DisposeAsync` wait for this task.

## Changes from the analysis

The analysis made these changes. They do not change the public API or the documented behaviour.

| Change | Before | After |
|---|---|---|
| `RetriableBenchmarks` makes its delegates and its strategy one time, not for each call. Thus, it measures only the library. | `Retry`: 88 B (0 failures), 520 B (2 failures). `RetryAsync`: 160 B (0 failures), 1,776 B (2 failures). | `Retry`: 0 B and 432 B. `RetryAsync`: 72 B and 1,688 B. The four values are the same as the values of a hand-written loop. |
| `CircuitBreaker` reads the state of a closed circuit without the lock. The state field is `volatile`. All changes to the state occur under the lock. | 101.0 ns for a success through a closed circuit (short job) | 76.6 ns, 0 B. The cost of the circuit breaker decreased by 29%. |
| `Exponential` and `Backoff` use `Math.ScaleB(1, n)`, not `Math.Pow(2, n)`. The result is the same power of 2. A test compares the two values for attempts 1 thru 1100. | `Exponential`, 9 failures: 140.7 ns. `RetryStrategies.Exponential`: 136.9 ns. | 103.0 ns and 91.5 ns. The cost of `Backoff` did not change, because `Random` causes most of its cost. |

The `RetriableBenchmarks` results after this change are not directly comparable with the 2.0 results. The 2.0 and
3.0 values in the table [Version 2.0 and version 3.0](#version-20-and-version-30) use the old benchmark code for
the two versions.

## Changes to the behaviour

Some changes to the behaviour of 3.0 changed the cost of the fallback, hedging and `ReliableTaskRunner`. We ran
`FallbackBenchmarks`, `HedgingBenchmarks`, `ReliableTaskRunnerBenchmarks`, `CircuitBreakerBenchmarks` and
`RetryTaskBenchmarks` with the short job, before and after these changes, one run after the other.

| Change | Benchmark | Before | After |
|---|---|---|---|
| The fallback applies only to the outcome of the attempts. For each call, a factory with a fallback allocates one 24 B object that records that outcome. | Fallback, set but not used | 36.2 ns, 0 B | 39.2 ns, 24 B |
| | Fallback, used for a result | 50.7 ns, 0 B | 53.7 ns, 24 B |
| | Fallback, used for an exception | 8.84 µs, 1,480 B | 8.96 µs, 1,504 B |
| Hedged attempts 2 and higher start on the thread pool. Each one allocates a task, a proxy task from `Unwrap` and a work item of the thread pool. | Hedging, the first attempt fails and the second succeeds | 2.62 µs, 824 B | 5.01 µs, 1,444 B |
| | Hedging, delay of zero, the attempt that loses is cancelled | 4.51 µs, 2,314 B | 2.60 µs, 2,951 B |
| | Hedging, the first attempt succeeds | 129 ns, 472 B | 150 ns, 496 B |
| Hedging does not start an attempt after an outcome wins. With a delay of zero, attempt 1 succeeds synchronously, thus attempt 2 does not start. | Hedging, delay of zero, two attempts | 211 ns, 608 B | 160 ns, 496 B |
| `ReliableTaskRunner` keeps a task for the restart decision, and starts each restart on the thread pool. | `Run` and `StopAsync` | 1.68 µs, 1.08 KB | 1.93 µs, 1.3 KB |
| | `Run` and `DisposeAsync` | 1.60 µs, 1.02 KB | 2.01 µs, 1.22 KB |
| | `Run`, a restart after 1 failure, and `StopAsync` | 8.54 µs, 2.59 KB | 14.06 µs, 2.77 KB |
| | `Run`, restarts after 4 failures, and `StopAsync` | 5.07 µs, 6.36 KB | 20.72 µs, 6.49 KB |

The time of the cancelled hedged attempt decreased, but this change is in the range of the noise. The time and the
memory of the circuit breaker did not change. The `RetryTask` results with 2 failures allocate less than 70 B more,
and their time changed less than the noise.

## Changes that we did not make

| Change | Reason |
|---|---|
| A synchronous path for an operation that completes immediately | Changes to `AsyncLocal` values and to the `ExecutionContext` would go back to the caller. Also, the cancellation would be reported differently. The gain is a few nanoseconds. |
| `PoolingAsyncValueTaskMethodBuilder` | A pooled `ValueTask` fails if the caller awaits it two times. |
| No `AsTask()` for the `Task` overloads | `AsTask()` allocates only when the operation completes asynchronously. This memory is small compared with the task of the operation. |
| Use the timeout `CancellationTokenSource` again for each call | A source that is used again can cancel a token that an earlier call still uses. |
| Less memory for hedging | The decrease is less than 100 B, in the code that has the most risk from concurrent attempts. |
| A `ValueTask` path for `HedgedTaskFactory`, or a `HedgingOptions` record kept in the factory | Hedging allocates approximately 1 KB when attempt 2 starts, thus the 150 B to 320 B that the `Task` adapters and the per-call record add are small. One `Task` shape for `OperationForAttempt` and `Run` keeps the API simple. The engine takes `HedgingOptions` so that the engine did not change in the reshape. |
| A rejection by the circuit breaker without an exception | The API contract is an exception. The cost comes from the exception through an `async` method. |
| A circuit breaker window without a lock, or a less accurate clock | The window must change all its counts at the same time. A less accurate clock changes the time of each bucket. |
| `Cancel()` in `StopAsync`, not `CancelAsync()` | This would change the documented behaviour of 3.0. |
