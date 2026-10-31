#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// RetryTask is Task with retries. RetryTask.Run is Task.Run: it runs an asynchronous operation once, with the default
// settings, and retries it when it throws. StartNew is Task.Factory.StartNew: it starts a synchronous operation on the
// thread pool and retries it there, without blocking a thread during the delays. The demo shows the one-off Run, then
// starts eight flaky operations at once with StartNew, watches them recover, and shows what the returned task carries
// when an operation gives up.
//
//   dotnet run Examples/retry-task.cs

using System.Diagnostics;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("RetryTask").Color(Color.Orange1).Centered());
AnsiConsole.Write(new Rule("[orange1]Task.Run and Task.Factory.StartNew, with retries[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A report client fails now and then. [white]RetryTask<T>.Run[/] retries one asynchronous call with the " +
        "default settings, in one line. Eight reports are then built by a [white]synchronous[/] client that fails from " +
        "[white]0 to 4[/] times each: [white]StartNew[/] runs all eight at once on the thread pool and retries each one, " +
        "with [white]5 retries, 150 ms apart[/]. The caller only awaits the tasks.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// ---- The one-off form: RetryTask<T>.Run ----
AnsiConsole.Write(new Rule("[orange1]RetryTask<T>.Run: one asynchronous call, the default settings[/]").LeftJustified());

// RetryTask.Run and RetryTask<T>.Run use the default factory: new Count() (5 tries, no delay) and AnyExceptionPolicy.
// Compare Retriable.RetryAsync, which has the same signature and makes 10 tries. The operation gets a token, and the
// first try runs on the caller's thread until its first await, as the operation would without retries.
FlakyClient summary = new("summary", failures: 2, workMs: 0);
var oneOff = Stopwatch.StartNew();
string headline = await RetryTask<string>.Run(ct => summary.BuildAsync(ct));
AnsiConsole.MarkupLineInterpolated($"  RetryTask<string>.Run: [white]{headline}[/] after [white]{summary.Attempts}[/] attempts, {oneOff.ElapsedMilliseconds} ms");

// The non-generic RetryTask.Run is for an operation that returns a Task with no result.
FlakyClient audit = new("audit", failures: 1, workMs: 0);
await RetryTask.Run(async ct => { await audit.BuildAsync(ct); });
AnsiConsole.MarkupLineInterpolated($"  RetryTask.Run:         [white]audit written[/] after [white]{audit.Attempts}[/] attempts");

// When the 5 tries run out, Run throws the exception of the last try itself, not an AggregateException.
FlakyClient offline = new("offline", failures: int.MaxValue, workMs: 0);
try
{
    await RetryTask<string>.Run(ct => offline.BuildAsync(ct));
}
catch (TransientException ex)
{
    AnsiConsole.MarkupLineInterpolated($"  Gave up:               [red]{ex.GetType().Name}[/] after [white]{offline.Attempts}[/] attempts: {ex.Message}");
}

AnsiConsole.MarkupLine("[grey]  For other settings, make a RetryTaskFactory<T> and call its Run: see timeouts-and-cancellation.cs.[/]");
AnsiConsole.WriteLine();

// ---- StartNew: a synchronous operation on the thread pool ----
AnsiConsole.Write(new Rule("[orange1]StartNew: eight synchronous operations on the thread pool[/]").LeftJustified());

// Constant: the same 150 ms before each retry, at most 5 retries (6 attempts), so every operation here recovers.
var strategy = RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromMilliseconds(150));
var policy = RetryPolicies.For<TransientException>();

// A configured factory holds the strategy and the policy, as new TaskFactory(...) holds its options. Keep it in a
// field. The StartNew overloads without a strategy or a policy use the factory's.
RetryTaskFactory<string> reports = new(strategy, policy);

int[] failures = [0, 3, 1, 4, 2, 0, 3, 1];
var clients = failures.Select((f, i) => new FlakyClient($"report {i + 1}", f)).ToArray();
var total = Stopwatch.StartNew();
List<Task> started = new();

await AnsiConsole.Progress()
    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new ElapsedTimeColumn())
    .StartAsync(async ctx =>
    {
        foreach (var client in clients)
        {
            // The bar fills one sixth per attempt and jumps to full when the operation succeeds.
            var bar = ctx.AddTask(client.Name, maxValue: 6);
            client.OnAttempt = succeeded => bar.Value = succeeded ? 6 : bar.Value + 1;

            // StartNew has the same shape as Task<T>.Factory.StartNew. An async lambda here is a compile error (CS0619):
            // the factory could retry only the start of the task, not its failure. For an asynchronous operation, use
            // Run instead, for example reports.Run(ct => client.BuildAsync(ct)).
            started.Add(client.Name == "report 8"

                // The default non-generic factory runs an Action, as Task.Factory.StartNew does. This overload takes
                // the strategy and the policy as arguments.
                ? RetryTask.Factory.StartNew(() => { client.Build(); }, strategy, policy)
                : reports.StartNew(client.Build));
        }

        await Task.WhenAll(started);
    });

var results = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("operation").AddColumn(new TableColumn("attempts").RightAligned()).AddColumn("status").AddColumn("result");
foreach (var (client, task) in clients.Zip(started))
{
    var result = task is Task<string> withResult ? withResult.Result : "(an Action: no result)";
    results.AddRow(client.Name, $"{client.Attempts}", $"[green]{task.Status}[/]", Markup.Escape(result));
}

AnsiConsole.Write(results);
AnsiConsole.MarkupLine($"[grey]  All eight ran side by side: {total.ElapsedMilliseconds} ms in total, not the sum of their retries.[/]");
AnsiConsole.WriteLine();

// ---- When an operation gives up ----
AnsiConsole.Write(new Rule("[orange1]When the retries run out[/]").LeftJustified());

// LongRunning: a hint to the scheduler that the operation blocks its thread for a long time, so it gets its own thread
// instead of a thread-pool thread. The retries keep the option.
FlakyClient broken = new("report 9", failures: int.MaxValue);
var doomed = reports.StartNew(broken.Build, TaskCreationOptions.LongRunning);

// RetryTask keeps at most 100 failures: the first 99 and the last. 150 tries with no delay show the cap.
FlakyClient hopeless = new("report 10", failures: int.MaxValue, workMs: 0);
var capped = reports.StartNew(hopeless.Build, new Count(150));

await AnsiConsole.Status().StartAsync("report 9 retries on its own thread…", async _ =>
{
    try
    {
        await Task.WhenAll(doomed, capped);
    }
    catch (AggregateException)
    {
        // A task that gives up faults with ONE AggregateException that holds the failure of each try, so await
        // throws that AggregateException. The tasks still hold everything, as rendered below.
    }
});

Tree faults = new("[orange1]Faulted tasks[/]");
foreach (var (name, task, client) in new[] { ("report 9 (6 tries)", doomed, broken), ("report 10 (150 tries)", capped, hopeless) })
{
    // task.Exception wraps the AggregateException of the tries; its InnerExceptions are the failures, in order.
    var inner = ((AggregateException)task.Exception!.InnerException!).InnerExceptions;
    var node = faults.AddNode(
        $"{name}: Status = [red]{task.Status}[/], {client.Attempts} attempts, AggregateException.InnerExceptions.Count = [white]{inner.Count}[/]");
    node.AddNode($"[grey][[0]][/] {Markup.Escape(inner[0].Message)}");
    if (inner.Count > 2)
    {
        node.AddNode($"[grey][[{inner.Count - 2}]][/] {Markup.Escape(inner[^2].Message)}");
    }

    node.AddNode($"[grey][[{inner.Count - 1}]][/] {Markup.Escape(inner[^1].Message)}");
}

AnsiConsole.Write(faults);
AnsiConsole.MarkupLine("[grey]  The cap keeps a long retry from holding thousands of exceptions.[/]");
AnsiConsole.MarkupLine("[grey]  The last failure is always kept.[/]");
AnsiConsole.WriteLine();

// ---- Settings that only Run can use ----
AnsiConsole.Write(new Rule("[orange1]StartNew refuses the settings that it cannot use[/]").LeftJustified());

// StartNew keeps its own scheduler-based loop, which has no timeouts, callback, breaker, result check or fallback. A
// factory with one of these settings makes every StartNew throw, so the setting is never ignored. The copy constructor
// makes a changed copy of a factory.
RetryTaskFactory<string> withTimeout = new(reports) { AttemptTimeout = TimeSpan.FromSeconds(1) };
try
{
    _ = withTimeout.StartNew(() => "never runs");
}
catch (InvalidOperationException ex)
{
    AnsiConsole.MarkupLineInterpolated($"  [red]InvalidOperationException[/]: {Markup.Escape(ex.Message)}");
}

// A report client with a synchronous API. It throws TransientException a set number of times, then succeeds.
public sealed class FlakyClient(string name, int failures, int workMs = 120)
{
    private int attempts;

    public string Name => name;

    public int Attempts => Volatile.Read(ref this.attempts);

    public string? Result { get; private set; }

    public Action<bool>? OnAttempt { get; set; }

    public string Build()
    {
        var attempt = Interlocked.Increment(ref this.attempts);
        if (workMs > 0)
        {
            Thread.Sleep(workMs);
        }

        if (attempt <= failures)
        {
            this.OnAttempt?.Invoke(false);
            throw new TransientException($"{name}: attempt {attempt} failed (the server was busy)");
        }

        this.OnAttempt?.Invoke(true);
        return this.Result = $"{name}: {attempt * 37 % 90 + 10} pages";
    }

    // The asynchronous form of the same client, for Run. The token is what a real client would pass to its I/O.
    public async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        return this.Build();
    }
}

public sealed class TransientException(string message) : Exception(message);
