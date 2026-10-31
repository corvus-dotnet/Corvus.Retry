#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Hedging cuts the long tail of a slow service: when an attempt has not answered within a short delay, the pipeline
// starts a second attempt in parallel and takes whichever answers first. The demo compares twenty calls with and
// without hedging, then sends the hedged attempt to a second region with HedgingActionGenerator.
//
//   dotnet run Examples/hedging.cs

using System.Collections.Concurrent;
using System.Diagnostics;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Hedging").Color(Color.DeepSkyBlue1).Centered());
AnsiConsole.Write(new Rule("[deepskyblue1]race a second attempt against a slow one[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A service usually answers in [white]100 ms[/], but [white]one call in five[/] takes [white]1.5 s[/]. " +
        "Retries do not help: the slow call does not fail, it is only slow. Hedging starts a second attempt after " +
        "[white]200 ms[/] and keeps the first answer.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

const int Calls = 20;

// Without hedging: no options at all would mean four attempts with a retry delay, so make it one attempt.
var plain = new ReliabilityPipeline(new ReliabilityPipelineOptions { Strategy = RetryStrategies.Constant(retries: 0, delay: TimeSpan.Zero) });

// With hedging: 200 ms is twice the usual latency, so a healthy call never starts a hedge, but a slow one does long
// before its 1.5 s are up. MaxHedgedAttempts = 2 permits a third attempt if the second one is also slow.
var hedgingEvents = new ConcurrentQueue<(int Call, HedgingEventArgs Args)>();
int currentCall = 0;
var hedged = new ReliabilityPipeline(new ReliabilityPipelineOptions
{
    Hedging = new HedgingOptions
    {
        Delay = TimeSpan.FromMilliseconds(200),
        MaxHedgedAttempts = 2,
        OnHedging = args =>
        {
            hedgingEvents.Enqueue((Volatile.Read(ref currentCall), args));
            return ValueTask.CompletedTask;
        },
    },
});

// The same seed for both runs, so both see the same long tail and the output repeats from run to run.
var plainService = new LongTailService(seed: 29);
var hedgedService = new LongTailService(seed: 29);
var plainTimes = new List<double>();
var hedgedTimes = new List<double>();
var hedgedCalls = new List<CallLog>();

var total = Stopwatch.StartNew();
await AnsiConsole.Progress()
    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new ElapsedTimeColumn())
    .StartAsync(async ctx =>
    {
        var plainBar = ctx.AddTask("without hedging", maxValue: Calls);
        var hedgedBar = ctx.AddTask("with hedging", maxValue: Calls);

        // The two runs go side by side to save time; inside each run the calls are sequential.
        await Task.WhenAll(
            RunAsync(Calls, plainBar, plainTimes, async () =>
            {
                var log = new CallLog();
                await plain.ExecuteAsync(ct => plainService.GetAsync(log, ct));
            }),
            RunAsync(Calls, hedgedBar, hedgedTimes, async () =>
            {
                var log = new CallLog();
                hedgedCalls.Add(log);
                Volatile.Write(ref currentCall, hedgedCalls.Count);
                await hedged.ExecuteAsync(ct => hedgedService.GetAsync(log, ct));
            }));
    });

var comparison = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("").AddColumn(new TableColumn("median").RightAligned()).AddColumn(new TableColumn("p90").RightAligned())
    .AddColumn(new TableColumn("max").RightAligned()).AddColumn(new TableColumn("total").RightAligned());
comparison.AddRow(StatsRow("without hedging", plainTimes, "grey"));
comparison.AddRow(StatsRow("with hedging", hedgedTimes, "deepskyblue1"));
AnsiConsole.Write(comparison);
AnsiConsole.WriteLine();

// Wait a moment so that the cancelled attempts have recorded their cancellation; the pipeline does not wait for them.
await Task.Delay(100);

// One hedged call, attempt by attempt: the first call in which attempt 1 was slow and attempt 2 won.
var slowIndex = hedgedCalls.FindIndex(c => c.Attempts is [{ Slow: true }, { Outcome: "won" }, ..]);
if (slowIndex >= 0)
{
    var call = hedgedCalls[slowIndex];
    var tree = new Tree($"[deepskyblue1]Hedged call {slowIndex + 1}[/] answered in {call.Attempts.Min(a => a.EndedAtMs ?? double.MaxValue):F0} ms");
    foreach (var attempt in call.Attempts)
    {
        var colour = attempt.Outcome == "won" ? "green" : attempt.Outcome == "cancelled" ? "grey" : "yellow";
        var node = tree.AddNode(
            $"attempt {attempt.Number}: started at {attempt.StartedAtMs:F0} ms, latency {attempt.LatencyMs} ms → [{colour}]{attempt.Outcome}[/]");
        var hedge = hedgingEvents.FirstOrDefault(e => e.Call == slowIndex + 1 && e.Args.AttemptNumber == attempt.Number);
        if (hedge.Call != 0)
        {
            node.AddNode($"[grey]OnHedging: AttemptNumber = {hedge.Args.AttemptNumber}, LastException = " +
                $"{Markup.Escape(hedge.Args.LastException?.GetType().Name ?? "null (the delay started it, not a failure)")}[/]");
        }
    }

    AnsiConsole.Write(tree);
    AnsiConsole.WriteLine();
}

// Part 2: HedgingActionGenerator sends each hedged attempt somewhere else. Attempt 1 uses the operation given to
// ExecuteAsync (the "eu" region, because the generator returns null for it); attempt 2 goes to "us".
AnsiConsole.Write(new Rule("[deepskyblue1]HedgingActionGenerator: hedge to another region[/]").LeftJustified());
var regions = new RegionService();
string? hedgeCause = null;
var regional = new ReliabilityPipeline<string>(new ReliabilityPipelineOptions<string>
{
    Hedging = new HedgingOptions
    {
        Delay = TimeSpan.FromMilliseconds(200),
        MaxHedgedAttempts = 1,
        OnHedging = args =>
        {
            hedgeCause = args.LastException is { } ex ? $"{ex.GetType().Name} (a failure)" : "null (the delay)";
            return ValueTask.CompletedTask;
        },
    },
    HedgingActionGenerator = attempt => attempt == 1 ? null : ct => regions.GetAsync("us", ct),
});

var regionTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("eu behaves").AddColumn("OnHedging LastException").AddColumn("answered by")
    .AddColumn(new TableColumn("time").RightAligned());
foreach (var behaviour in new[] { "fast", "slow", "fails" })
{
    regions.EuBehaviour = behaviour;
    hedgeCause = null;
    var watch = Stopwatch.StartNew();
    var answer = await regional.ExecuteAsync(ct => regions.GetAsync("eu", ct));
    regionTable.AddRow(behaviour, Markup.Escape(hedgeCause ?? "no hedge"), $"[deepskyblue1]{answer}[/]", $"{watch.ElapsedMilliseconds} ms");
}

AnsiConsole.Write(regionTable);
AnsiConsole.MarkupLine("[grey]  A failure starts the hedge at once; a slow answer, after the 200 ms delay.[/]");
AnsiConsole.MarkupLine($"[grey]  Hedging trades extra calls ({hedgedService.Invocations} instead of {plainService.Invocations}) for a short tail.[/]");
AnsiConsole.MarkupLine($"[grey]  The operation must be idempotent. Whole demo: {total.Elapsed.TotalSeconds:F1} s.[/]");

static async Task RunAsync(int calls, ProgressTask bar, List<double> times, Func<Task> call)
{
    for (var i = 0; i < calls; i++)
    {
        var watch = Stopwatch.StartNew();
        await call();
        times.Add(watch.Elapsed.TotalMilliseconds);
        bar.Increment(1);
    }
}

static string[] StatsRow(string name, List<double> times, string colour)
{
    var sorted = times.Order().ToArray();
    return
    [
        $"[{colour}]{name}[/]",
        $"{sorted[sorted.Length / 2]:F0} ms",
        $"{sorted[(int)Math.Ceiling(sorted.Length * 0.9) - 1]:F0} ms",
        $"{sorted[^1]:F0} ms",
        $"{times.Sum() / 1000:F1} s",
    ];
}

// What happened to each attempt of one call.
public sealed class CallLog
{
    private readonly Stopwatch watch = Stopwatch.StartNew();

    public List<AttemptLog> Attempts { get; } = [];

    public double NowMs => this.watch.Elapsed.TotalMilliseconds;
}

public sealed class AttemptLog
{
    public int Number { get; init; }

    public double StartedAtMs { get; init; }

    public int LatencyMs { get; init; }

    public bool Slow => this.LatencyMs > 1000;

    public double? EndedAtMs { get; set; }

    public string Outcome { get; set; } = "running";
}

// A service with a long tail: one invocation in five takes 1.5 s, the rest take 100 ms. A seeded Random keeps the
// shape the same on every run.
public sealed class LongTailService(int seed)
{
    private readonly Random random = new(seed);
    private readonly Lock gate = new();

    public int Invocations { get; private set; }

    public async ValueTask<int> GetAsync(CallLog log, CancellationToken cancellationToken)
    {
        AttemptLog attempt;
        lock (this.gate)
        {
            this.Invocations++;
            attempt = new AttemptLog
            {
                Number = log.Attempts.Count + 1,
                StartedAtMs = log.NowMs,
                LatencyMs = this.random.Next(5) == 0 ? 1500 : 100,
            };
            log.Attempts.Add(attempt);
        }

        try
        {
            await Task.Delay(attempt.LatencyMs, cancellationToken);
            attempt.Outcome = "won";
            attempt.EndedAtMs = log.NowMs;
            return attempt.Number;
        }
        catch (OperationCanceledException)
        {
            // The pipeline cancels the token of each losing attempt as soon as another attempt wins.
            attempt.Outcome = "cancelled";
            throw;
        }
    }
}

// Two regions. The test sets how "eu" behaves; "us" always answers in 120 ms.
public sealed class RegionService
{
    public string EuBehaviour { get; set; } = "fast";

    public async ValueTask<string> GetAsync(string region, CancellationToken cancellationToken)
    {
        if (region == "eu" && this.EuBehaviour == "fails")
        {
            await Task.Delay(50, cancellationToken);
            throw new HttpRequestException("eu: 503 Service Unavailable");
        }

        await Task.Delay(region == "eu" && this.EuBehaviour == "slow" ? 1500 : region == "eu" ? 80 : 120, cancellationToken);
        return region;
    }
}
