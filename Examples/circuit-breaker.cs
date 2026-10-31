#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Shows one CircuitBreaker shared by two factories that call the same failing service. When failures through either
// factory cross the threshold, the circuit opens for both: calls fail fast instead of queueing up behind a dead
// dependency, and one test call decides when the service is back. The breaker's policy decides which exceptions are
// failures: a 404 is the caller's mistake, not the service's, so it does not count.
//
//   dotnet run Examples/circuit-breaker.cs

using System.Diagnostics;
using System.Net;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Breaker").Color(Color.HotPink).Centered());
AnsiConsole.Write(new Rule("[hotpink]one breaker, two factories, one failing service[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "The orders page ([white]factory A[/]) and the stock checker ([white]factory B[/]) both call the inventory " +
        "service. When [white]half[/] of at least [white]4[/] recent calls fail, stop calling it for [white]1 s[/], " +
        "then let one test call through to see whether it has recovered.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var clock = Stopwatch.StartNew();
var changes = new List<(TimeSpan At, CircuitStateChange Change)>();
// The settings are constructor parameters, because the breaker starts its clock when it is constructed.
var breaker = new CircuitBreaker(

    // Real services use larger numbers (the defaults are 10 % of at least 100 calls in 30 s, and a 5 s open duration).
    // These small ones make the circuit open after a handful of calls, and close again within the demo.
    failureRate: 0.5,
    minimumCalls: 4,
    samplingWindow: TimeSpan.FromSeconds(10),
    openDuration: TimeSpan.FromSeconds(1),

    // The policy selects the exceptions that are failures. TransientHttp accepts a 5xx, a 408 or a 429, a connection
    // error and an attempt timeout; it refuses a 404, so a lookup of a missing item counts as a successful call. The
    // default, AnyExceptionPolicy, would open the circuit for a run of 404s.
    policy: RetryPolicies.TransientHttp,
    onStateChanged: change =>
    {
        changes.Add((clock.Elapsed, change));
        return ValueTask.CompletedTask;
    });

// Two independent factories share the one breaker instance, and so share its state. DoNotRetry makes each call one
// attempt, so that each row of the call log is exactly one call that the breaker counts or rejects.
var factoryA = new RetryTaskFactory(new DoNotRetry(), AnyExceptionPolicy.Default) { CircuitBreaker = breaker };
var factoryB = new RetryTaskFactory(new DoNotRetry(), AnyExceptionPolicy.Default) { CircuitBreaker = breaker };

var service = new InventoryService();
var calls = new List<CallRecord>();
var callCount = 0;

await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Driving the inventory service…", async _ =>
{
    Phase("The service is healthy");
    foreach (var (name, factory) in new[] { ("A", factoryA), ("B", factoryB), ("A", factoryA), ("B", factoryB) })
    {
        await Call(name, factory);
    }

    Phase("The service goes down");
    service.IsDown = true;
    foreach (var (name, factory) in new[] { ("B", factoryB), ("A", factoryA), ("B", factoryB), ("A", factoryA) })
    {
        await Call(name, factory);
    }

    // The failure that opened the circuit came through A, but B is rejected at once: they share the breaker.
    Phase("Open: B fails fast too");
    await Call("B", factoryB);
    for (var i = 0; i < 4; i++)
    {
        await Task.Delay(200);
        await Call(i % 2 == 0 ? "A" : "B", i % 2 == 0 ? factoryA : factoryB);
    }

    // Wait out the rest of the break. The service recovers meanwhile, but is slow on its first call.
    var opened = changes.Last().At;
    var remaining = opened + TimeSpan.FromMilliseconds(1100) - clock.Elapsed;
    await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    service.IsDown = false;
    service.LatencyMs = 300;

    Phase("Half-open: one test call");
    var testCall = Call("A", factoryA, " (test call)");
    await Task.Delay(100);
    await Call("B", factoryB);
    await testCall;
    service.LatencyMs = 20;

    Phase("Manual control");
    breaker.Isolate();
    await Call("A", factoryA);
    breaker.Close();
    await Call("B", factoryB);

    // Close() also cleared the counts. Four 404s in a row are four calls at a 100 % exception rate, but the breaker's
    // policy refuses a 404, so each one counts as a success and the circuit stays closed.
    Phase("A 404 is not a failure: the policy refuses it");
    service.IsMissing = true;
    foreach (var (name, factory) in new[] { ("A", factoryA), ("B", factoryB), ("A", factoryA), ("B", factoryB) })
    {
        await Call(name, factory);
    }
});

var log = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[hotpink]Call log[/]")
    .AddColumn("#").AddColumn(new TableColumn("At").RightAligned()).AddColumn("Via").AddColumn("Outcome").AddColumn("RetryAfter").AddColumn("Circuit");
foreach (var call in calls)
{
    if (call.Via.Length == 0)
    {
        log.AddRow("", "", "", $"[grey]── {Markup.Escape(call.Outcome)} ──[/]", "", "");
        continue;
    }

    log.AddRow(call.Number.ToString(), Seconds(call.At), call.Via, call.Outcome, call.RetryAfter, Coloured(call.StateAfter));
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
AnsiConsole.MarkupLine("[grey]  The 404s are calls that succeeded, as far as the breaker's policy is concerned.[/]");

// Calls the service through one factory and records what happened. The row is added when the call starts, so that
// overlapping calls are listed in the order in which they started.
async Task Call(string name, RetryTaskFactory factory, string note = "")
{
    var number = ++callCount;
    var at = clock.Elapsed;
    calls.Add(new(number, at, name, "pending", "", default));
    var index = calls.Count - 1;

    string outcome;
    var retryAfter = "";
    try
    {
        await factory.Run(ct => service.GetStockAsync(ct));
        outcome = $"[green]succeeded[/]{note}";
    }
    catch (CircuitIsolatedException)
    {
        outcome = "[mediumpurple]CircuitIsolatedException[/]";
    }
    catch (CircuitRejectedException ex)
    {
        retryAfter = ex.RetryAfter is { } wait ? $"{wait.TotalMilliseconds:F0} ms" : "null";
        outcome = "[red]CircuitRejectedException[/]";
    }
    catch (HttpRequestException ex)
    {
        outcome = $"[yellow]failed: {(int)ex.StatusCode.GetValueOrDefault()}[/]";
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
sealed record CallRecord(int Number, TimeSpan At, string Via, string Outcome, string RetryAfter, CircuitState StateAfter);

// A simulated inventory service that can go down and come back, with no network. It honours the cancellation token.
// With IsMissing set, the item is not in the catalogue: the service answers 404, which the client throws.
sealed class InventoryService
{
    public bool IsDown { get; set; }

    public bool IsMissing { get; set; }

    public int LatencyMs { get; set; } = 20;

    public async Task<int> GetStockAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(this.LatencyMs, cancellationToken);
        if (this.IsDown)
        {
            throw new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        }

        if (this.IsMissing)
        {
            throw new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound);
        }

        return 42;
    }
}
