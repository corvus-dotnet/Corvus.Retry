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
- `RetryTask` and `ReliableTaskRunner.StopAsync` are slower than in 2.0, because of changes to their behaviour.
- A `ReliabilityPipeline` with no options adds approximately 26 ns to a call, and allocates no memory.
- A hedged attempt after the first attempt starts on the thread pool. In the benchmark, this adds approximately 620 B
  and 2.4 µs to a hedged call.

## Test environment

| Item | Value |
|---|---|
| Hardware | Intel Core i7-13700H laptop (6 performance cores and 8 efficiency cores, 20 logical processors), High performance power plan |
| Operating system | Windows 11 10.0.26300 |
| .NET | SDK 10.0.403, runtime 10.0.14, X64 RyuJIT x86-64-v3 |
| BenchmarkDotNet | 0.15.8, with `[MemoryDiagnoser]` on all the benchmark classes |
| Date | October 2026 |
| Version 2.0 | Commit `03c4590` |
| Version 3.0 | Branch `features/v3` |

## Method

1. We ran all the benchmarks with `--job short` on 2.0 (137 cases) and then on 3.0 (255 cases). No other builds or
   tests ran between the two runs.
2. We ran the benchmark classes for the hot paths again with the default job, one version after the other. These
   classes are `RetriableBenchmarks`, `RetriableOverloadBenchmarks`, `StrategyBenchmarks`, `PolicyBenchmarks`,
   `RetryTaskBenchmarks` and `ReliabilityPipelineBenchmarks`. The two versions used the same benchmark code.
3. After the changes that this document describes in [Changes from the analysis](#changes-from-the-analysis), we ran
   the benchmarks that these changes affect again. Each run used the same job as the run before the change.
4. After the changes in [Changes to the behaviour](#changes-to-the-behaviour), we ran the benchmarks that these
   changes affect with the short job, before and after the changes.

Some benchmarks have a different name in 3.0:

| 2.0 | 3.0 |
|---|---|
| `SleepBenchmarks` | `DelayBenchmarks` |
| `Retry_WithDelay_Sleep` | `Retry_WithDelay_ThreadSleep` |
| `RetryAsync_WithDelay_SleepAsync` | `RetryAsync_WithDelay_TaskDelay` |
| `RetryStrategyBenchmarks` | Removed, together with the `RetryStrategy` base class |

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

The last column gives a benchmark in the same class, for comparison.

| Benchmark | 3.0 | Comparison |
|---|---|---|
| Pipeline, no options | 34.8 ns, 0 B | Hand-written loop: 8.6 ns, 0 B |
| Pipeline, `AttemptTimeout` | 152 ns, 144 B | |
| Pipeline, `TotalTimeout` | 154 ns, 144 B | |
| Pipeline, `AttemptTimeout` and `TotalTimeout` | 189 ns, 432 B | |
| Pipeline, 2 failures | 4.01 µs, 432 B | Hand-written loop: 3.85 µs, 432 B |
| Pipeline, 2 failures, with `OnRetry` (short job) | 3.64 µs, 432 B | The callback allocates no memory. `RetryEventArgs` is a value type. |
| `ReliabilityPipeline<T>`, one result retry | 47 ns, 24 B | The 24 B is a boxed `int` in `RetryAttempt.Result`. |
| Circuit breaker, closed circuit, success | 76.6 ns, 0 B | No circuit breaker: 23.1 ns |
| Circuit breaker, closed circuit, failure | 9.3 µs, 1,480 B | No circuit breaker: 9.0 µs, 1,480 B |
| Circuit breaker, open or isolated circuit (the call is rejected) | 5.6 µs, 920 B | |
| Fallback, set but not used (short job) | 39.2 ns, 24 B | No fallback: 25.8 ns, 0 B. The 24 B is the object that records the outcome of the attempts. |
| Fallback, used for a result (short job) | 53.7 ns, 24 B | No fallback: 25.7 ns, 0 B |
| Fallback, used for an exception (short job) | 8.96 µs, 1,504 B | No fallback: 8.65 µs, 1,480 B |
| Hedging, the first attempt succeeds (short job) | 150 ns, 496 B | Pipeline with retries: 24.4 ns, 0 B |
| Hedging, the first attempt fails and the second succeeds (short job) | 5.01 µs, 1,444 B | Pipeline with retries: 2.18 µs, 216 B. Attempt 2 starts on the thread pool. |
| Hedging, delay of zero, attempt 1 succeeds synchronously (short job) | 160 ns, 496 B | Attempt 2 does not start, because attempt 1 won before it. |
| Hedging, delay of zero, the attempt that loses is cancelled (short job) | 2.60 µs, 2,951 B | |
| `ReliableTaskRunner.Run` and `DisposeAsync` (short job) | 2.01 µs, 1.22 KB | |
| `RetryTask`, delay of one tick, 2 failures | 15.46 µs, 4,119 B | |

Exceptions cause most of the cost of a failure. A rejection by the circuit breaker is an exception, thus it costs
approximately the same as a failure of the operation.

## Causes of the slower results

We accept these results. Each one is the result of a change to the behaviour of 3.0.

- **R1: an asynchronous `Retriable` call that completes immediately takes 3 ns to 22 ns more.** It allocates the same
  memory or 40 B less. In 3.0, one retry loop does all the asynchronous calls. This loop also examines the token, the
  timeouts and the callbacks, thus its state machine is larger. If the operation completes asynchronously, this
  difference is not important: the `DelayBenchmarks` asynchronous cases are 40% faster in 3.0.
- **R2: `RetryTask` with failures takes 26% more time and allocates 233 B more.** Each `StartNew` overload allocates
  16 B or 32 B more. In 3.0, a retry does not block a thread. The scheduler runs each retry as a continuation. The state
  for each call is in the factory, not in the strategy.
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
| The fallback applies only to the outcome of the attempts. For each call, a pipeline with a fallback allocates one 24 B object that records that outcome. | Fallback, set but not used | 36.2 ns, 0 B | 39.2 ns, 24 B |
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
| A rejection by the circuit breaker without an exception | The API contract is an exception. The cost comes from the exception through an `async` method. |
| A circuit breaker window without a lock, or a less accurate clock | The window must change all its counts at the same time. A less accurate clock changes the time of each bucket. |
| `Cancel()` in `StopAsync`, not `CancelAsync()` | This would change the documented behaviour of 3.0. |
