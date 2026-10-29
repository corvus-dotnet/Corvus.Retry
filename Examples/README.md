# Corvus.Retry demos

A set of standalone [Spectre.Console](https://spectreconsole.net/) demos for `Corvus.Retry`, each a
single-file [.NET file-based app](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10#file-based-apps).
No project, no solution entry, just run the file from the repository root:

```bash
dotnet run Examples/retry-basics.cs
```

The only prerequisite is the .NET 10 SDK. Each file declares its own dependencies at the top
(`#:package Spectre.Console` and `#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj`), so the demos
build against the library source in this repository. The first run builds the library and takes about
20 seconds; later runs take a few seconds each.

Every demo is self-contained and works offline. The dependency it retries against (a flaky service, an
`HttpClient` with a scripted handler, a queue) is simulated in a few lines at the bottom of the file, so
the retry behaviour is the only thing that varies. Where randomness is the point, as with jitter, the
footnote says so; otherwise the output is repeatable.

## The demos

| File | What it shows |
|------|---------------|
| [`menu.cs`](menu.cs) | An interactive launcher: pick a demo from a Spectre menu and it runs the others. |
| [`retry-basics.cs`](retry-basics.cs) | A flaky service drops its first calls: `Retriable` turns that into a result, a callback shows each retry and its delay, a cancellation token stops the retries early, and a service that never recovers ends in its own last exception. |
| [`strategies.cs`](strategies.cs) | Every built-in strategy's delay sequence side by side, without waiting, showing tries versus retries, how `maxDelay` caps growth and how jitter changes exponential backoff. |
| [`policies.cs`](policies.cs) | Which exceptions are worth another try: every policy and combinator against the same eight exceptions, then a live run that stops retrying when a 503 turns into a permanent 404. |
| [`thundering-herd.cs`](thundering-herd.cs) | Fifty clients lose the same database at the same moment: without jitter they retry in lockstep spikes of 50, and with jitter they spread out. |
| [`timeouts-and-cancellation.cs`](timeouts-and-cancellation.cs) | An attempt timeout turns a slow call into a retry, a total timeout caps the whole call, and a cancellation by the caller is never retried. |
| [`result-retries.cs`](result-retries.cs) | Retry 503 and 429 responses that do not throw, wait as long as `Retry-After` asks, and dispose each rejected response. |
| [`fallback.cs`](fallback.cs) | Return a cached price when the catalogue service is down, while bugs in your own request or callbacks still reach the caller. |
| [`circuit-breaker.cs`](circuit-breaker.cs) | One breaker shared by two pipelines opens on failures from either, fails fast, tests recovery with one call, and supports manual isolation. |
| [`hedging.cs`](hedging.cs) | A service with a long tail answers most calls in 100 ms but one in five in 1.5 s; hedging starts a second attempt after 200 ms and keeps the first answer, cutting p90 from 1.5 s to about 300 ms. |
| [`composed-pipelines.cs`](composed-pipelines.cs) | A pipeline cannot hedge and retry at once, so an outer retry pipeline with a fallback wraps an inner hedging pipeline behind a circuit breaker, traced attempt by attempt. |
| [`retry-task.cs`](retry-task.cs) | Eight flaky synchronous operations started at once with `RetryTask.Factory.StartNew` recover on the thread pool, and a task that gives up carries every failure, capped at 100. |
| [`message-queue-consumer.cs`](message-queue-consumer.cs) | Keep a queue consumer loop alive with `ReliableTaskRunner`: restart after a transient fault with a delay, stop on a poison message, and shut down cleanly. |
| [`http-client-resilience.cs`](http-client-resilience.cs) | One pipeline around an `HttpClient` call that gets 503, a hang, 429 with `Retry-After` and 500 before 200; it retries, obeys the header, times out each attempt, and falls back to a cached answer when a circuit breaker opens. |
| [`database-startup.cs`](database-startup.cs) | Wait for a database that is still starting by retrying a wrapped `SocketException` with backoff, and fail at once on a wrong password. |
| [`testing-with-fake-time.cs`](testing-with-fake-time.cs) | Nine minutes of `Backoff` delays and a 30 s attempt timeout tested in milliseconds with a `FakeTimeProvider`, advancing the clock only after the library has created its timer. |

A good order for a first read: the first four cover the building blocks (`Retriable`, strategies, policies,
jitter); the next six add one `ReliabilityPipeline` feature each; the last five are complete scenarios.

## Running everything

```bash
# interactive launcher
dotnet run Examples/menu.cs

# every demo in order, with a summary of exit codes (for scripts and CI)
dotnet run Examples/menu.cs -- --all

# one demo through the launcher
dotnet run Examples/menu.cs -- circuit-breaker

# or one at a time
dotnet run Examples/retry-basics.cs
dotnet run Examples/strategies.cs
dotnet run Examples/policies.cs
dotnet run Examples/thundering-herd.cs
dotnet run Examples/timeouts-and-cancellation.cs
dotnet run Examples/result-retries.cs
dotnet run Examples/fallback.cs
dotnet run Examples/circuit-breaker.cs
dotnet run Examples/hedging.cs
dotnet run Examples/composed-pipelines.cs
dotnet run Examples/retry-task.cs
dotnet run Examples/message-queue-consumer.cs
dotnet run Examples/http-client-resilience.cs
dotnet run Examples/database-startup.cs
dotnet run Examples/testing-with-fake-time.cs
```

Output is styled when run in a terminal and degrades to plain text when redirected (piped to a file or
another program), so the demos are safe to capture in scripts. The launcher's menu needs an interactive
terminal; without one it runs every demo, or the one you name, and exits with code 1 if any demo failed.
