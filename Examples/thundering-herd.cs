#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Why jitter exists. When many clients lose a dependency at the same moment and all use the same exponential backoff,
// they retry in lockstep and hit the recovering service with one spike after another. Jitter spreads them out.
//
//   dotnet run Examples/thundering-herd.cs

using System.Net.Http;

using Spectre.Console;

using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Herd").Color(Color.HotPink).Centered());
AnsiConsole.Write(new Rule("[hotpink]50 clients, one outage, the same backoff[/]"));
AnsiConsole.WriteLine();

const int Clients = 50;
var bucket = TimeSpan.FromMilliseconds(250);
var window = TimeSpan.FromSeconds(8);

AnsiConsole.Write(new Panel(
        $"At t = 0 a database fails, and [white]{Clients}[/] clients see the error at the same instant. Each retries " +
        "with [white]RetryStrategies.Exponential(retries: 5, baseDelay: 1 s)[/].\nWithout jitter, every client " +
        "computes the same delays, so all of them come back together, just as the database tries to recover.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// One strategy instance serves every client: a strategy keeps no state for a call. The delays are computed, not
// waited for, so the demo shows a 30-second outage in an instant.
var plain = RetryStrategies.Exponential(retries: 5, baseDelay: TimeSpan.FromSeconds(1));
var jittered = RetryStrategies.Exponential(retries: 5, baseDelay: TimeSpan.FromSeconds(1), useJitter: true);

// Decorrelated jitter draws each delay from the base delay up to three times the previous one, so without a cap a
// client can wait minutes. maxDelay keeps the spread but bounds the wait.
var capped = RetryStrategies.Exponential(retries: 5, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(8), useJitter: true);

var plainInstants = Enumerable.Range(0, Clients).SelectMany(_ => RetryInstants(plain)).ToList();
var jitteredInstants = Enumerable.Range(0, Clients).SelectMany(_ => RetryInstants(jittered)).ToList();
var cappedInstants = Enumerable.Range(0, Clients).SelectMany(_ => RetryInstants(capped)).ToList();

var plainBuckets = Histogram(plainInstants, bucket);
var jitteredBuckets = Histogram(jitteredInstants, bucket);
var cappedBuckets = Histogram(cappedInstants, bucket);

var grid = new Grid().AddColumn().AddColumn();
grid.AddRow(
    Chart("[white]No jitter[/]", plainBuckets, Color.Grey),
    Chart("[hotpink]useJitter: true[/]", jitteredBuckets, Color.HotPink));
AnsiConsole.MarkupLine($"[grey]  Retries reaching the database in each {bucket.TotalMilliseconds:0} ms slot, first {window.TotalSeconds:0} s[/]");
AnsiConsole.Write(grid);
AnsiConsole.WriteLine();

var summary = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("").AddColumn(new TableColumn("No jitter").RightAligned()).AddColumn(new TableColumn("useJitter: true").RightAligned())
    .AddColumn(new TableColumn("+ maxDelay: 8 s").RightAligned());
summary.AddRow("Retries in total", $"{plainInstants.Count}", $"{jitteredInstants.Count}", $"{cappedInstants.Count}");
summary.AddRow($"Peak in one {bucket.TotalMilliseconds:0} ms slot", $"[red]{plainBuckets.Values.Max()}[/]", $"[green]{jitteredBuckets.Values.Max()}[/]", $"[green]{cappedBuckets.Values.Max()}[/]");
summary.AddRow("Slots with any retry", $"{plainBuckets.Count}", $"{jitteredBuckets.Count}", $"{cappedBuckets.Count}");
summary.AddRow("Last retry", $"{plainInstants.Max().TotalSeconds:0.0} s", $"{jitteredInstants.Max().TotalSeconds:0.0} s", $"{cappedInstants.Max().TotalSeconds:0.0} s");
AnsiConsole.Write(summary);

AnsiConsole.MarkupLine($"[grey]  Without jitter: spikes of {Clients} at 1, 3, 7, 15, 31 s (delays 1, 2, 4, 8, 16 s).[/]");
AnsiConsole.MarkupLine($"[grey]  Peak per slot: {plainBuckets.Values.Max()} without jitter, {jitteredBuckets.Values.Max()} with it. The spread differs on every run.[/]");
AnsiConsole.MarkupLine("[grey]  Uncapped jitter can push the last retry minutes out; maxDelay bounds it.[/]");

// The instants at which one client retries, measured from the failure at t = 0. Every retry fails too, because the
// dependency is still down. Each returned delay goes back in as PreviousDelay, which decorrelated jitter depends on.
static IEnumerable<TimeSpan> RetryInstants(IRetryStrategy strategy)
{
    var failure = new HttpRequestException("connection refused");
    var now = TimeSpan.Zero;
    var previous = TimeSpan.Zero;
    for (var attempt = 1; strategy.GetRetryDelay(new RetryAttempt(attempt, failure, null, previous)) is TimeSpan delay; attempt++)
    {
        now += delay;
        previous = delay;
        yield return now;
    }
}

// Counts the retries in each slot. The key is the slot number: slot 4 holds the retries from 1.00 s to 1.25 s.
static Dictionary<long, int> Histogram(IEnumerable<TimeSpan> instants, TimeSpan slot) =>
    instants.GroupBy(t => t.Ticks / slot.Ticks).ToDictionary(g => g.Key, g => g.Count());

BarChart Chart(string label, Dictionary<long, int> buckets, Color color)
{
    var chart = new BarChart().Width(38).Label(label).WithMaxValue(Clients);
    for (long slot = 0; slot < window.Ticks / bucket.Ticks; slot++)
    {
        chart.AddItem($"{TimeSpan.FromTicks(slot * bucket.Ticks).TotalSeconds:0.00} s", buckets.GetValueOrDefault(slot), color);
    }

    return chart;
}
