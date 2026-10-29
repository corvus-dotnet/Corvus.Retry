#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2
#:package Microsoft.Extensions.TimeProvider.Testing@10.11.0

// A test of retry code must not wait for the retries. ReliabilityPipeline takes its delays and timeouts from a
// TimeProvider, so a test gives it a FakeTimeProvider and moves the clock itself: nine minutes of backoff and a
// 30-second attempt timeout pass in a few milliseconds. The demo is written the way a test reads.
//
//   dotnet run Examples/testing-with-fake-time.cs

using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Fake time").Color(Color.SpringGreen3).Centered());
AnsiConsole.Write(new Rule("[springgreen3]test minutes of retries in milliseconds[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A service fails [white]five times[/]. The Backoff strategy waits [white]about nine minutes[/] in total " +
        "before the sixth attempt. A test cannot wait that long, and it must not depend on how fast the build agent is. " +
        "With a FakeTimeProvider the test decides when time passes.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var wallClock = Stopwatch.StartNew();

// ---- Test 1: the call succeeds on the sixth attempt, after five Backoff delays ----

// Arrange. MaxBackoff is raised from 30 s to 5 min so the delays really do add up to minutes.
var time = new TimerAwareFakeTimeProvider();
var service = new FlakyService(failures: 5);
var delays = new List<TimeSpan>();
var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
{
    Strategy = new Backoff(maxTries: 6, deltaBackoff: TimeSpan.FromSeconds(10)) { MaxBackoff = TimeSpan.FromMinutes(5) },
    TimeProvider = time,
    OnRetry = args =>
    {
        delays.Add(args.DelayBeforeRetry);
        return ValueTask.CompletedTask;
    },
});

// Act. Start the call, but do not await it yet: it is about to wait for the first delay.
var call = pipeline.ExecuteAsync(service.GetAsync).AsTask();

var steps = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("retry").AddColumn(new TableColumn("Backoff delay").RightAligned())
    .AddColumn("1 ms early").AddColumn("after Advance(delay)").AddColumn(new TableColumn("fake clock").RightAligned());
for (var retry = 1; retry <= 5; retry++)
{
    // THE PITFALL: Advance only fires timers that exist. OnRetry runs BEFORE the pipeline creates the delay timer,
    // so a test that advances from OnRetry, or as soon as OnRetry signals, can move the clock first; the timer is
    // then created relative to the new time, the advance is lost and the call waits for ever. Awaiting Task.Yield()
    // narrows the race but does not close it. The safe pattern: wait until the library has created the timer.
    await time.WaitForTimersAsync(retry);

    var delay = delays[retry - 1];
    var callsBefore = service.Calls;

    // Task.Delay measures whole milliseconds, so "just too early" is one millisecond short, not one tick.
    time.Advance(delay - TimeSpan.FromMilliseconds(1));
    var early = service.Calls == callsBefore ? "[green]✓ still waiting[/]" : "[red]✗ started early[/]";

    time.Advance(TimeSpan.FromMilliseconds(1));
    if (retry < 5)
    {
        await time.WaitForTimersAsync(retry + 1);
    }
    else
    {
        await call;
    }

    var outcome = service.Calls == callsBefore + 1
        ? $"[green]✓ attempt {service.Calls} ran, {(retry < 5 ? "failed" : "succeeded")}[/]"
        : "[red]✗ no attempt[/]";
    steps.AddRow($"{retry}", $"{delay.TotalSeconds:F1} s", early, outcome, $"{time.Elapsed:m\\:ss\\.f}");
}

// Assert.
AnsiConsole.Write(steps);
AnsiConsole.MarkupLine($"  [springgreen3]✓[/] result: [white]{Markup.Escape(await call)}[/] after " +
    $"[white]{time.Elapsed:m\\:ss}[/] of fake time");
AnsiConsole.WriteLine();

// ---- Test 2: an attempt that hangs fails with RetryTimeoutException when its AttemptTimeout expires ----
AnsiConsole.Write(new Rule("[springgreen3]AttemptTimeout without waiting[/]").LeftJustified());

// Arrange. One attempt (no retries) with a 30 s limit; the operation never answers unless it is cancelled.
var time2 = new TimerAwareFakeTimeProvider();
var timeoutPipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
{
    Strategy = RetryStrategies.Constant(retries: 0, delay: TimeSpan.Zero),
    AttemptTimeout = TimeSpan.FromSeconds(30),
    TimeProvider = time2,
});

// Act. The attempt timeout is a timer too, so the same rule applies: wait for it, then advance.
var hung = timeoutPipeline.ExecuteAsync(ct => new ValueTask(Task.Delay(Timeout.Infinite, ct))).AsTask();
await time2.WaitForTimersAsync(1);
time2.Advance(TimeSpan.FromSeconds(29.999));
var stillRunning = !hung.IsCompleted;
time2.Advance(TimeSpan.FromMilliseconds(1));

// Assert.
var timeout = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("check").AddColumn("outcome");
timeout.AddRow("after 29.999 s", stillRunning ? "[green]✓ the attempt is still running[/]" : "[red]✗ already finished[/]");
try
{
    await hung;
    timeout.AddRow("after 30 s", "[red]✗ no exception[/]");
}
catch (RetryTimeoutException ex)
{
    timeout.AddRow("after 30 s", $"[green]✓ {ex.GetType().Name}[/]: {Markup.Escape(ex.Message)}");
    timeout.AddRow("ex.Timeout", $"[green]✓ {ex.Timeout}[/]");
}

AnsiConsole.Write(timeout);
AnsiConsole.MarkupLine($"[grey]  {time.Elapsed + TimeSpan.FromSeconds(30):m\\:ss} of fake time passed in [white]{wallClock.ElapsedMilliseconds} ms[/] of real time. Backoff is randomised, so the delays differ from run to run.[/]");

// A service that fails a set number of times, then succeeds. It answers at once: the only waiting is the pipeline's.
public sealed class FlakyService(int failures)
{
    private int calls;

    public int Calls => Volatile.Read(ref this.calls);

    public ValueTask<string> GetAsync(CancellationToken cancellationToken) =>
        Interlocked.Increment(ref this.calls) <= failures
            ? ValueTask.FromException<string>(new HttpRequestException("503 Service Unavailable"))
            : ValueTask.FromResult($"200 OK on attempt {this.Calls}");
}

// A FakeTimeProvider that tells the test when the code under test has created a timer. This is test code, not part
// of the library: FakeTimeProvider.CreateTimer is virtual, so a subclass can count the timers.
public sealed class TimerAwareFakeTimeProvider : FakeTimeProvider
{
    private readonly Lock gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> waiters = [];
    private readonly DateTimeOffset start;
    private int created;

    public TimerAwareFakeTimeProvider() => this.start = this.GetUtcNow();

    public TimeSpan Elapsed => this.GetUtcNow() - this.start;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Count the timer only once the base class has scheduled it, so a test that sees the count can advance.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (this.gate)
        {
            this.created++;
            foreach (var waiter in this.waiters.Where(w => w.Count <= this.created).ToList())
            {
                waiter.Signal.TrySetResult();
                this.waiters.Remove(waiter);
            }
        }

        return timer;
    }

    // Completes when at least `count` timers exist. The 5 s real-time limit turns a test bug into a failure, not a hang.
    public Task WaitForTimersAsync(int count)
    {
        lock (this.gate)
        {
            if (this.created >= count)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiters.Add((count, signal));
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
