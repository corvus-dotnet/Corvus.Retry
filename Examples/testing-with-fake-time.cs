#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2
#:package Microsoft.Extensions.TimeProvider.Testing@10.11.0

// A test of retry code must not wait for the retries. RetryTaskFactory and CircuitBreaker take their delays, timeouts and
// windows from a TimeProvider, so a test gives them a FakeTimeProvider and moves the clock itself: nine minutes of
// backoff, a 30-second attempt timeout, and a breaker's 30-second sampling window and 5-second open duration pass in a
// few milliseconds. The demo is written the way a test reads.
//
//   dotnet run Examples/testing-with-fake-time.cs

using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
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
var factory = new RetryTaskFactory
{
    Strategy = new Backoff(maxTries: 6, deltaBackoff: TimeSpan.FromSeconds(10)) { MaxBackoff = TimeSpan.FromMinutes(5) },
    TimeProvider = time,
    OnRetry = args =>
    {
        delays.Add(args.DelayBeforeRetry);
        return ValueTask.CompletedTask;
    },
};

// Act. Start the call, but do not await it yet: it is about to wait for the first delay. GetAsync returns a ValueTask,
// so the call uses RunValueTask. For an operation that returns a Task, use Run.
var call = factory.RunValueTask(service.GetAsync).AsTask();

var steps = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("retry").AddColumn(new TableColumn("Backoff delay").RightAligned())
    .AddColumn("1 ms early").AddColumn("after Advance(delay)").AddColumn(new TableColumn("fake clock").RightAligned());
for (var retry = 1; retry <= 5; retry++)
{
    // THE PITFALL: Advance only fires timers that exist. OnRetry runs BEFORE the factory creates the delay timer,
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
var timeoutFactory = new RetryTaskFactory
{
    Strategy = RetryStrategies.Constant(retries: 0, delay: TimeSpan.Zero),
    AttemptTimeout = TimeSpan.FromSeconds(30),
    TimeProvider = time2,
};

// Act. The attempt timeout is a timer too, so the same rule applies: wait for it, then advance.
var hung = timeoutFactory.Run(ct => Task.Delay(Timeout.Infinite, ct));
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
AnsiConsole.WriteLine();

// ---- Test 3: a CircuitBreaker's sampling window and open duration without waiting ----
AnsiConsole.Write(new Rule("[springgreen3]CircuitBreaker without waiting[/]").LeftJustified());

// Arrange. The breaker reads the clock of its TimeProvider; it creates no timer, so the plain FakeTimeProvider is enough.
// The settings are the defaults for the window (30 s) and the open duration (5 s), with a small call count.
var time3 = new FakeTimeProvider();
var changes = new List<string>();
var breaker = new CircuitBreaker(
    failureRate: 0.5,
    minimumCalls: 4,
    onStateChanged: change =>
    {
        changes.Add($"{change.From} → {change.To}");
        return ValueTask.CompletedTask;
    },
    timeProvider: time3);
var guarded = new RetryTaskFactory(new DoNotRetry(), AnyExceptionPolicy.Default) { CircuitBreaker = breaker, TimeProvider = time3 };

// Act and assert, step by step. Each call is one attempt that fails or succeeds at once.
var breakerStart = time3.GetUtcNow();
var circuit = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("step").AddColumn(new TableColumn("fake clock").RightAligned()).AddColumn("outcome").AddColumn("State");

async Task Step(string step, params bool[] calls)
{
    var outcome = "";
    foreach (var succeeds in calls)
    {
        try
        {
            await guarded.Run(ct => succeeds ? Task.CompletedTask : Task.FromException(new HttpRequestException("503 Service Unavailable")));
            outcome = "[green]✓ the call went through[/]";
        }
        catch (HttpRequestException)
        {
            outcome = "[red]✗ 503 (counted as a failure)[/]";
        }
        catch (CircuitRejectedException ex)
        {
            outcome = $"[yellow]CircuitRejectedException, RetryAfter = {ex.RetryAfter?.TotalMilliseconds:F0} ms[/]";
        }
    }

    circuit.AddRow(step, $"{time3.GetUtcNow() - breakerStart:m\\:ss\\.fff}", outcome, breaker.State.ToString());
}

await Step("3 failures: fewer than minimumCalls", false, false, false);

// The sampling window is 30 s: after 31 s, the three failures are too old to count.
time3.Advance(TimeSpan.FromSeconds(31));
await Step("Advance(31 s), then 1 failure: the window forgot the first three", false);

// Three more failures make 4 calls in the window, all failures: the circuit opens on the fourth.
await Step("3 more failures: 4 of 4 in the window, the circuit opens", false, false, false);

// The open duration is 5 s. Just before it ends, a call is still rejected, and RetryAfter says how long remains.
time3.Advance(TimeSpan.FromSeconds(4.999));
await Step("Advance(4.999 s), then a call: still open", true);

// One millisecond later, the next call is the test call. It succeeds, so the circuit closes.
time3.Advance(TimeSpan.FromMilliseconds(1));
await Step("Advance(1 ms), then a call: the test call succeeds", true);

AnsiConsole.Write(circuit);
AnsiConsole.MarkupLine($"  OnStateChanged: [white]{string.Join(", ", changes)}[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine($"[grey]  {time.Elapsed + TimeSpan.FromSeconds(30) + (time3.GetUtcNow() - breakerStart):m\\:ss} of fake time passed in [white]{wallClock.ElapsedMilliseconds} ms[/] of real time.[/]");
AnsiConsole.MarkupLine("[grey]  Backoff is randomised, so the delays in test 1 differ from run to run.[/]");

// A service that fails a set number of times, then succeeds. It answers at once: the only waiting is the factory's.
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
