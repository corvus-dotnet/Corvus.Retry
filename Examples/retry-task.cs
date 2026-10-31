#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// RetryTask is Task.Factory.StartNew with retries: it starts a synchronous operation on the thread pool and retries it
// there, without blocking a thread during the delays. The demo starts eight flaky operations at once, watches them
// recover, and then shows what the returned task carries when an operation gives up.
//
//   dotnet run Examples/retry-task.cs

using System.Diagnostics;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("RetryTask").Color(Color.Orange1).Centered());
AnsiConsole.Write(new Rule("[orange1]Task.Factory.StartNew, with retries[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "Eight reports are built by a [white]synchronous[/] client that fails now and then: from [white]0 to 4[/] " +
        "times each. RetryTask runs all eight at once on the thread pool and retries each one, with " +
        "[white]5 retries, 150 ms apart[/]. The caller only awaits the tasks.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// Constant: the same 150 ms before each retry, at most 5 retries (6 attempts), so every operation here recovers.
var strategy = RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromMilliseconds(150));
var policy = RetryPolicies.Handle<TransientException>();

int[] failures = [0, 3, 1, 4, 2, 0, 3, 1];
var clients = failures.Select((f, i) => new FlakyClient($"report {i + 1}", f)).ToArray();
var total = Stopwatch.StartNew();
var started = new List<Task>();

await AnsiConsole.Progress()
    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new ElapsedTimeColumn())
    .StartAsync(async ctx =>
    {
        foreach (var client in clients)
        {
            // The bar fills one sixth per attempt and jumps to full when the operation succeeds.
            var bar = ctx.AddTask(client.Name, maxValue: 6);
            client.OnAttempt = succeeded => bar.Value = succeeded ? 6 : bar.Value + 1;

            // RetryTask<T>.Factory.StartNew has the same shape as Task<T>.Factory.StartNew, plus a strategy and a policy.
            // An async lambda here is a compile error (CS0619): the factory could retry only the start of the task,
            // not its failure. For an asynchronous operation, use Retriable.RetryAsync instead.
            started.Add(client.Name == "report 8"

                // The non-generic factory runs an Action, as Task.Factory.StartNew does.
                ? RetryTask.Factory.StartNew(() => { client.Build(); }, strategy, policy)
                : RetryTask<string>.Factory.StartNew(client.Build, strategy, policy));
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
var broken = new FlakyClient("report 9", failures: int.MaxValue);
var doomed = RetryTask<string>.Factory.StartNew(broken.Build, TaskCreationOptions.LongRunning, strategy, policy);

// RetryTask keeps at most 100 failures: the first 99 and the last. 150 tries with no delay show the cap.
var hopeless = new FlakyClient("report 10", failures: int.MaxValue, workMs: 0);
var capped = RetryTask<string>.Factory.StartNew(hopeless.Build, new Count(150), policy);

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

var faults = new Tree("[orange1]Faulted tasks[/]");
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
}

public sealed class TransientException(string message) : Exception(message);
