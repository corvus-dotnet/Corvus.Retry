#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Hedging cuts the long tail of a slow service: when an attempt has not answered within a short stagger, the factory
// starts a second attempt in parallel and takes whichever answers first. The demo compares twenty calls with and
// without hedging, sends the hedged attempt to a second region with OperationForAttempt, shows the one-off
// HedgedTask<T>.Run, and hedges a 503 response (not an exception) with ResultIsFailure and a Fallback.
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

// Without hedging: the slow call does not fail, so a retry would not help. Make it one attempt.
RetryTaskFactory plain = new() { Strategy = new DoNotRetry() };

// With hedging: a stagger of 200 ms is twice the usual latency, so a healthy call never starts a hedge, but a slow one
// does long before its 1.5 s are up. extraAttempts: 2 permits a third attempt if the second one is also slow.
ConcurrentQueue<(int Call, HedgedAttemptEventArgs Args)> hedgingEvents = new();
int currentCall = 0;
HedgedTaskFactory hedged = new(extraAttempts: 2, stagger: TimeSpan.FromMilliseconds(200))
{
    OnHedgedAttempt = args =>
    {
        hedgingEvents.Enqueue((Volatile.Read(ref currentCall), args));
        return ValueTask.CompletedTask;
    },
};

// The same seed for both runs, so both see the same long tail and the output repeats from run to run.
LongTailService plainService = new(seed: 29);
LongTailService hedgedService = new(seed: 29);
List<double> plainTimes = new();
List<double> hedgedTimes = new();
List<CallLog> hedgedCalls = new();

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
                CallLog log = new();
                await plain.Run(ct => plainService.GetAsync(log, ct));
            }),
            RunAsync(Calls, hedgedBar, hedgedTimes, async () =>
            {
                CallLog log = new();
                hedgedCalls.Add(log);
                Volatile.Write(ref currentCall, hedgedCalls.Count);
                await hedged.Run(ct => hedgedService.GetAsync(log, ct));
            }));
    });

var comparison = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("").AddColumn(new TableColumn("median").RightAligned()).AddColumn(new TableColumn("p90").RightAligned())
    .AddColumn(new TableColumn("max").RightAligned()).AddColumn(new TableColumn("total").RightAligned());
comparison.AddRow(StatsRow("without hedging", plainTimes, "grey"));
comparison.AddRow(StatsRow("with hedging", hedgedTimes, "deepskyblue1"));
AnsiConsole.Write(comparison);
AnsiConsole.WriteLine();

// Wait a moment so that the cancelled attempts have recorded their cancellation; the factory does not wait for them.
await Task.Delay(100);

// One hedged call, attempt by attempt: the first call in which attempt 1 was slow and attempt 2 won.
var slowIndex = hedgedCalls.FindIndex(c => c.Attempts is [{ Slow: true }, { Outcome: "won" }, ..]);
if (slowIndex >= 0)
{
    var call = hedgedCalls[slowIndex];
    Tree tree = new($"[deepskyblue1]Hedged call {slowIndex + 1}[/] answered in {call.Attempts.Min(a => a.EndedAtMs ?? double.MaxValue):F0} ms");
    foreach (var attempt in call.Attempts)
    {
        var colour = attempt.Outcome == "won" ? "green" : attempt.Outcome == "cancelled" ? "grey" : "yellow";
        var node = tree.AddNode(
            $"attempt {attempt.Number}: started at {attempt.StartedAtMs:F0} ms, latency {attempt.LatencyMs} ms → [{colour}]{attempt.Outcome}[/]");
        var hedge = hedgingEvents.FirstOrDefault(e => e.Call == slowIndex + 1 && e.Args.AttemptNumber == attempt.Number);
        if (hedge.Call != 0)
        {
            node.AddNode($"[grey]OnHedgedAttempt: AttemptNumber = {hedge.Args.AttemptNumber}, LastException = " +
                $"{Markup.Escape(hedge.Args.LastException?.GetType().Name ?? "null (the stagger started it, not a failure)")}[/]");
        }
    }

    AnsiConsole.Write(tree);
    AnsiConsole.WriteLine();
}

// Part 2: OperationForAttempt sends each hedged attempt somewhere else. Attempt 1 uses the operation given to Run
// (the "eu" region, because the function returns null for it); attempt 2 goes to "us".
AnsiConsole.Write(new Rule("[deepskyblue1]OperationForAttempt: hedge to another region[/]").LeftJustified());
RegionService regions = new();
string? hedgeCause = null;
HedgedTaskFactory<string> regional = new(extraAttempts: 1, stagger: TimeSpan.FromMilliseconds(200))
{
    OnHedgedAttempt = args =>
    {
        hedgeCause = args.LastException is { } ex ? $"{ex.GetType().Name} (a failure)" : "null (the stagger)";
        return ValueTask.CompletedTask;
    },
    OperationForAttempt = attempt => attempt == 1 ? null : ct => regions.GetAsync("us", ct),
};

var regionTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("eu behaves").AddColumn("OnHedgedAttempt LastException").AddColumn("answered by")
    .AddColumn(new TableColumn("time").RightAligned());
foreach (var behaviour in new[] { "fast", "slow", "fails" })
{
    regions.EuBehaviour = behaviour;
    hedgeCause = null;
    var watch = Stopwatch.StartNew();
    var answer = await regional.Run(ct => regions.GetAsync("eu", ct));
    regionTable.AddRow(behaviour, Markup.Escape(hedgeCause ?? "no hedge"), $"[deepskyblue1]{answer}[/]", $"{watch.ElapsedMilliseconds} ms");
}

AnsiConsole.Write(regionTable);
AnsiConsole.MarkupLine("[grey]  A failure starts the hedge at once; a slow answer, after the 200 ms stagger.[/]");
AnsiConsole.WriteLine();

// Part 3: the one-off form. HedgedTask<T>.Run uses the default factory: 1 extra attempt, a 2 s stagger, and any
// exception is a failure. A failure starts the next attempt at once, so a call that fails once and then answers does not
// wait for the stagger. Both attempts run the operation given to Run.
AnsiConsole.Write(new Rule("[deepskyblue1]HedgedTask<T>.Run: the default factory[/]").LeftJustified());
regions.EuBehaviour = "fails once";
var oneOff = Stopwatch.StartNew();
var oneOffAnswer = await HedgedTask<string>.Run(ct => regions.GetAsync("eu", ct));
AnsiConsole.MarkupLineInterpolated($"  eu fails once: answered by [deepskyblue1]{oneOffAnswer}[/] on attempt 2 in {oneOff.ElapsedMilliseconds} ms, not after the 2 s stagger");
AnsiConsole.WriteLine();

// Part 4: a response that says 503 does not throw, so Policy never sees it. ResultIsFailure marks it as a failure: the
// factory starts the next attempt at once, as after an exception, and disposes the rejected result. When every attempt
// is rejected, the caller gets the last result, unless a Fallback replaces it.
AnsiConsole.Write(new Rule("[deepskyblue1]ResultIsFailure and Fallback: hedge a 503 response[/]").LeftJustified());
string? fallbackSaw = null;
HedgedTaskFactory<Reply> statusAware = new(extraAttempts: 1, stagger: TimeSpan.FromMilliseconds(200))
{
    ResultIsFailure = reply => reply.Status == 503,
    OperationForAttempt = attempt => attempt == 1 ? null : ct => regions.GetReplyAsync("us", ct),
    OnHedgedAttempt = args =>
    {
        hedgeCause = args.LastException is { } ex ? ex.GetType().Name : "null";
        return ValueTask.CompletedTask;
    },

    // The fallback is the outermost part of the call. AppliesToResult sees the last result when every attempt was
    // rejected; OnFallback runs before the factory disposes that result, and the action after.
    Fallback = new(_ => ValueTask.FromResult(new Reply(200, "cache")))
    {
        AppliesToResult = reply => reply.Status == 503,
        OnFallback = args =>
        {
            fallbackSaw = $"{args.Result!.Status} from {args.Result.Region}";
            return ValueTask.CompletedTask;
        },
    },
};

var statusTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("eu says").AddColumn("us says").AddColumn("hedge started").AddColumn("OnFallback saw").AddColumn("answered by")
    .AddColumn(new TableColumn("time").RightAligned());
foreach (var (eu, us) in new[] { ("200", "200"), ("503", "200"), ("503", "503") })
{
    regions.EuBehaviour = eu;
    regions.UsBehaviour = us;
    hedgeCause = null;
    fallbackSaw = null;
    var watch = Stopwatch.StartNew();
    var reply = await statusAware.Run(ct => regions.GetReplyAsync("eu", ct));
    var hedgeStarted = hedgeCause is null ? "no hedge" : "at once, after the 503";
    statusTable.AddRow(eu, us, hedgeStarted, fallbackSaw ?? "-", $"[deepskyblue1]{reply.Status} from {reply.Region}[/]", $"{watch.ElapsedMilliseconds} ms");
}

AnsiConsole.Write(statusTable);
AnsiConsole.MarkupLine("[grey]  A rejected result starts the hedge at once, like a failure; OnHedgedAttempt gets LastException null.[/]");
AnsiConsole.MarkupLine($"[grey]  Hedging trades extra calls ({hedgedService.Invocations} instead of {plainService.Invocations} in part 1) for a short tail.[/]");
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

    public async Task<int> GetAsync(CallLog log, CancellationToken cancellationToken)
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
            // The factory cancels the token of each losing attempt as soon as another attempt wins.
            attempt.Outcome = "cancelled";
            throw;
        }
    }
}

// A response with a status code, for the ResultIsFailure part. A 503 is an ordinary result, not an exception.
public sealed record Reply(int Status, string Region);

// Two regions. The test sets how each behaves: "fast", "slow", "fails", "fails once" (the first call only), or "503"
// (answers with a 503 reply, without throwing). "us" answers in 120 ms, "eu" in 80 ms.
public sealed class RegionService
{
    private bool euHasFailed;

    public string EuBehaviour
    {
        get;
        set
        {
            field = value;
            this.euHasFailed = false;
        }
    } = "fast";

    public string UsBehaviour { get; set; } = "fast";

    public async Task<string> GetAsync(string region, CancellationToken cancellationToken)
    {
        var behaviour = region == "eu" ? this.EuBehaviour : this.UsBehaviour;
        if (behaviour == "fails" || (behaviour == "fails once" && !this.euHasFailed))
        {
            this.euHasFailed = true;
            await Task.Delay(50, cancellationToken);
            throw new HttpRequestException($"{region}: 503 Service Unavailable");
        }

        await Task.Delay(behaviour == "slow" ? 1500 : region == "eu" ? 80 : 120, cancellationToken);
        return region;
    }

    public async Task<Reply> GetReplyAsync(string region, CancellationToken cancellationToken)
    {
        var behaviour = region == "eu" ? this.EuBehaviour : this.UsBehaviour;
        await Task.Delay(region == "eu" ? 80 : 120, cancellationToken);
        return new(behaviour == "503" ? 503 : 200, region);
    }
}
