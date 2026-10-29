#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Shows one CircuitBreaker shared by two pipelines that call the same failing service. When failures through either
// pipeline cross the threshold, the circuit opens for both: calls fail fast instead of queueing up behind a dead
// dependency, and one test call decides when the service is back.
//
//   dotnet run Examples/circuit-breaker.cs

using System.Diagnostics;
using System.Net;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Breaker").Color(Color.HotPink).Centered());
AnsiConsole.Write(new Rule("[hotpink]one breaker, two pipelines, one failing service[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "The orders page ([white]pipeline A[/]) and the stock checker ([white]pipeline B[/]) both call the inventory " +
        "service. When [white]half[/] of at least [white]4[/] recent calls fail, stop calling it for [white]1 s[/], " +
        "then let one test call through to see whether it has recovered.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var clock = Stopwatch.StartNew();
var changes = new List<(TimeSpan At, CircuitStateChange Change)>();
var breaker = new CircuitBreaker(new CircuitBreakerOptions
{
    // Real services use larger numbers (the defaults are 10 % of at least 100 calls in 30 s, and a 5 s break).
    // These small ones make the circuit open after a handful of calls, and close again within the demo.
    FailureRatio = 0.5,
    MinimumThroughput = 4,
    SamplingDuration = TimeSpan.FromSeconds(10),
    BreakDuration = TimeSpan.FromSeconds(1),
    OnStateChanged = change =>
    {
        changes.Add((clock.Elapsed, change));
        return ValueTask.CompletedTask;
    },
});

// Two independent pipelines share the one breaker instance, and so share its state. DoNotRetry makes each call one
// attempt, so that each row of the call log is exactly one call that the breaker counts or rejects.
var pipelineA = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker });
var pipelineB = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = new DoNotRetry(), CircuitBreaker = breaker });

var service = new InventoryService();
var calls = new List<CallRecord>();
var callCount = 0;

await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Driving the inventory service…", async _ =>
{
    Phase("The service is healthy");
    foreach (var (name, pipeline) in new[] { ("A", pipelineA), ("B", pipelineB), ("A", pipelineA), ("B", pipelineB) })
    {
        await Call(name, pipeline);
    }

    Phase("The service goes down");
    service.IsDown = true;
    foreach (var (name, pipeline) in new[] { ("B", pipelineB), ("A", pipelineA), ("B", pipelineB), ("A", pipelineA) })
    {
        await Call(name, pipeline);
    }

    // The failure that opened the circuit came through A, but B is rejected at once: they share the breaker.
    Phase("Open: B fails fast too");
    await Call("B", pipelineB);
    for (var i = 0; i < 4; i++)
    {
        await Task.Delay(200);
        await Call(i % 2 == 0 ? "A" : "B", i % 2 == 0 ? pipelineA : pipelineB);
    }

    // Wait out the rest of the break. The service recovers meanwhile, but is slow on its first call.
    var opened = changes.Last().At;
    var remaining = opened + TimeSpan.FromMilliseconds(1100) - clock.Elapsed;
    await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    service.IsDown = false;
    service.LatencyMs = 300;

    Phase("Half-open: one test call");
    var testCall = Call("A", pipelineA, " (test call)");
    await Task.Delay(100);
    await Call("B", pipelineB);
    await testCall;
    service.LatencyMs = 20;

    Phase("Manual control");
    breaker.Isolate();
    await Call("A", pipelineA);
    breaker.Close();
    await Call("B", pipelineB);
});

var log = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[hotpink]Call log[/]")
    .AddColumn("#").AddColumn(new TableColumn("At").RightAligned()).AddColumn("Via").AddColumn("Outcome").AddColumn("RetryAfter").AddColumn("Circuit");
foreach (var call in calls)
{
    if (call.Pipeline.Length == 0)
    {
        log.AddRow("", "", "", $"[grey]── {Markup.Escape(call.Outcome)} ──[/]", "", "");
        continue;
    }

    log.AddRow(call.Number.ToString(), Seconds(call.At), call.Pipeline, call.Outcome, call.RetryAfter, Coloured(call.StateAfter));
}

AnsiConsole.Write(log);
AnsiConsole.WriteLine();

var timeline = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[hotpink]OnStateChanged[/]")
    .AddColumn(new TableColumn("At").RightAligned()).AddColumn("From").AddColumn("To").AddColumn("LastException");
foreach (var (at, change) in changes)
{
    var cause = change.LastException is { } ex ? $"{ex.GetType().Name}: {ex.Message}" : "-";
    timeline.AddRow(Seconds(at), Coloured(change.From), Coloured(change.To), Markup.Escape(cause));
}

AnsiConsole.Write(timeline);
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  RetryAfter counts down to the end of the break, and is null while half-open.[/]");
AnsiConsole.MarkupLine("[grey]  Open → HalfOpen happens on the first call after the break, not when it ends.[/]");

// Calls the service through one pipeline and records what happened. The row is added when the call starts, so that
// overlapping calls are listed in the order in which they started.
async Task Call(string name, ReliabilityPipeline pipeline, string note = "")
{
    var number = ++callCount;
    var at = clock.Elapsed;
    calls.Add(new(number, at, name, "pending", "", default));
    var index = calls.Count - 1;

    string outcome;
    var retryAfter = "";
    try
    {
        await pipeline.ExecuteAsync(ct => service.GetStockAsync(ct));
        outcome = $"[green]succeeded[/]{note}";
    }
    catch (IsolatedCircuitException)
    {
        outcome = "[mediumpurple]IsolatedCircuitException[/]";
    }
    catch (BrokenCircuitException ex)
    {
        retryAfter = ex.RetryAfter is { } wait ? $"{wait.TotalMilliseconds:F0} ms" : "null";
        outcome = "[red]BrokenCircuitException[/]";
    }
    catch (HttpRequestException)
    {
        outcome = "[yellow]failed: 503[/]";
    }

    calls[index] = new(number, at, name, outcome, retryAfter, breaker.State);
}

void Phase(string title) => calls.Add(new(0, clock.Elapsed, "", title, "", default));

static string Seconds(TimeSpan at) => $"{at.TotalSeconds:F2} s";

static string Coloured(CircuitState state) => state switch
{
    CircuitState.Closed => "[green]Closed[/]",
    CircuitState.Open => "[red]Open[/]",
    CircuitState.HalfOpen => "[yellow]HalfOpen[/]",
    _ => "[mediumpurple]Isolated[/]",
};

// One row of the call log. Number is 0 for a phase heading, whose Outcome holds the heading text.
sealed record CallRecord(int Number, TimeSpan At, string Pipeline, string Outcome, string RetryAfter, CircuitState StateAfter);

// A simulated inventory service that can go down and come back, with no network. It honours the cancellation token.
sealed class InventoryService
{
    public bool IsDown { get; set; }

    public int LatencyMs { get; set; } = 20;

    public async ValueTask<int> GetStockAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(this.LatencyMs, cancellationToken);
        if (this.IsDown)
        {
            throw new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        }

        return 42;
    }
}
