#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// A queue consumer is a loop that must run until the app stops. ReliableTaskRunner restarts the loop after a transient
// fault, waits between restarts, stops for good on a poison message instead of reading it again and again, and
// cancels the loop's token for a clean shutdown.
//
//   dotnet run Examples/message-queue-consumer.cs

using System.Collections.Concurrent;
using System.Diagnostics;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Queue").Color(Color.Orange1).Centered());
AnsiConsole.Write(new Rule("[orange1]keep a consumer loop alive, but not at any cost[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A consumer loop reads [white]20[/] orders from a queue. Order [white]7[/] meets a store timeout once. Order " +
        "[white]15[/] is not valid JSON, so it fails every time it is read. The runner must restart the loop after the " +
        "timeout, with a delay, and stop on the poison message instead of crashing on it again and again.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var clock = Stopwatch.StartNew();
var queue = new SimulatedQueue(count: 20);
var consumer = new OrderConsumer(queue, clock);

// No limit on the number of restarts, because the consumer must keep going; the delay starts at 200 ms and doubles,
// and maxDelay caps it at 1 s so that a recovered store is not left idle for long.
var restartStrategy = RetryStrategies.Exponential(retries: int.MaxValue, baseDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromSeconds(1));

// Restart after anything except a poison message: reading that message again cannot succeed.
var restartPolicy = RetryPolicies.Handle<PoisonMessageException>().Not();

AnsiConsole.Write(new Rule("[orange1]1. Run(runFunction, restartStrategy, retryPolicy)[/]").LeftJustified());
await using (var runner = ReliableTaskRunner.Run(consumer.RunAsync, restartStrategy, restartPolicy))
{
    await consumer.Poisoned.Task;

    // The strategy would wait 400 ms before a third run. Wait longer, to show that the policy refused the restart.
    await Task.Delay(600);
    AnsiConsole.MarkupLine($"  [grey]600 ms after the poison message: still {consumer.Runs.Count} runs, so no restart.[/]");

    try
    {
        await runner.StopAsync();
    }
    catch (PoisonMessageException ex)
    {
        // StopAsync reports how the last run ended: here, with the exception that the policy refused.
        AnsiConsole.MarkupLine($"  StopAsync faulted with [red]{ex.GetType().Name}[/]: {Markup.Escape(ex.Message)}");
    }
}

var summary = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[orange1]Runs[/]")
    .AddColumn("Run").AddColumn(new TableColumn("Started").RightAligned()).AddColumn(new TableColumn("Processed").RightAligned())
    .AddColumn("Ended with").AddColumn(new TableColumn("Delay before restart").RightAligned());
for (var i = 0; i < consumer.Runs.Count; i++)
{
    var run = consumer.Runs[i];
    var delay = i + 1 < consumer.Runs.Count ? $"{(consumer.Runs[i + 1].Started - run.Ended).TotalMilliseconds:F0} ms" : "[red]refused[/]";
    summary.AddRow(run.Number.ToString(), $"{run.Started.TotalMilliseconds:F0} ms", run.Processed.ToString(), $"[red]{run.Error}[/]", delay);
}

AnsiConsole.Write(summary);
AnsiConsole.MarkupLine(
    $"  [white]{consumer.Runs.Count}[/] runs, [white]{consumer.Runs.Count - 1}[/] restart, [white]{consumer.Processed}[/] orders processed, " +
    $"[white]{queue.Count}[/] left in the queue.");
AnsiConsole.WriteLine();

// An operator moves the poison message to the dead-letter queue, and the consumer starts again.
queue.DeadLetter();
AnsiConsole.Write(new Rule("[orange1]2. Run(runFunction) and a graceful shutdown[/]").LeftJustified());

// The simplest overload restarts at once after every failure, with no limit. If the loop failed at each start (the
// store is down), it would restart continuously and use a whole processor core: see the CAUTION in the README.
// Use it only for a loop that handles its own errors.
await using (var runner = ReliableTaskRunner.Run(consumer.RunAsync))
{
    await consumer.Drained.Task;
    AnsiConsole.MarkupLine("  [grey]The queue is empty and the loop is waiting for orders. Stopping the runner.[/]");

    // StopAsync cancels the token that the loop received. The loop sees it, and returns normally. Leaving the
    // await using scope would do the same, but it does not report how the run ended.
    await runner.StopAsync();
    AnsiConsole.MarkupLine($"  StopAsync completed. The loop's token was cancelled: [white]{consumer.SawCancellation}[/].");
}

AnsiConsole.MarkupLine(
    $"  Total: [white]{consumer.Processed}[/] orders processed, [white]{queue.DeadLetters.Count}[/] dead-lettered (order {string.Join(", ", queue.DeadLetters)}).");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  Order 7 was not acknowledged when it failed, so run 2 read it again.[/]");

// What happened in one run of the consumer loop.
internal sealed record RunRecord(int Number, TimeSpan Started)
{
    public TimeSpan Ended { get; set; }

    public int Processed { get; set; }

    public string Error { get; set; } = "";
}

// The message that the handler can never process.
internal sealed class PoisonMessageException(string message) : Exception(message);

// A queue with peek and acknowledge: a message stays in the queue until the consumer acknowledges it.
internal sealed class SimulatedQueue(int count)
{
    private readonly ConcurrentQueue<int> messages = new(Enumerable.Range(1, count));

    public List<int> DeadLetters { get; } = [];

    public int Count => this.messages.Count;

    public bool TryPeek(out int id) => this.messages.TryPeek(out id);

    public void Acknowledge() => this.messages.TryDequeue(out _);

    public void DeadLetter()
    {
        if (this.messages.TryDequeue(out var id))
        {
            this.DeadLetters.Add(id);
        }
    }
}

// The consumer loop. The runner calls RunAsync for each run, with a token that StopAsync cancels.
internal sealed class OrderConsumer(SimulatedQueue queue, Stopwatch clock)
{
    private bool storeTimedOut;

    public List<RunRecord> Runs { get; } = [];

    public int Processed { get; private set; }

    public bool SawCancellation { get; private set; }

    public TaskCompletionSource Poisoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var run = new RunRecord(this.Runs.Count + 1, clock.Elapsed);
        this.Runs.Add(run);
        Log(run, "", "[grey]run started[/]");
        try
        {
            while (true)
            {
                if (!queue.TryPeek(out var id))
                {
                    this.Drained.TrySetResult();
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                await this.HandleAsync(id, cancellationToken);
                queue.Acknowledge();
                this.Processed++;
                run.Processed++;
                Log(run, $"order {id:00}", "[green]processed[/]");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A graceful shutdown: return normally, so that StopAsync completes without an exception.
            this.SawCancellation = true;
            Log(run, "", "[orange1]token cancelled: the loop returns[/]");
        }
        catch (Exception ex)
        {
            run.Ended = clock.Elapsed;
            run.Error = ex.GetType().Name;
            queue.TryPeek(out var id);
            Log(run, $"order {id:00}", $"[red]{ex.GetType().Name}: {Markup.Escape(ex.Message)}[/]");
            if (ex is PoisonMessageException)
            {
                this.Poisoned.TrySetResult();
            }

            throw;
        }
    }

    private static void Log(RunRecord run, string message, string outcome) =>
        AnsiConsole.MarkupLine($"  run {run.Number}  {message,-8}  {outcome}");

    // Simulated work: order 7 times out the first time only, and order 15 always fails.
    private async Task HandleAsync(int id, CancellationToken cancellationToken)
    {
        await Task.Delay(50, cancellationToken);
        if (id == 7 && !this.storeTimedOut)
        {
            this.storeTimedOut = true;
            throw new TimeoutException("the order store did not answer");
        }

        if (id == 15)
        {
            throw new PoisonMessageException("order 15 is not valid JSON");
        }
    }
}
