#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// A strategy decides how many attempts a call gets and how long to wait before each retry. This demo asks every
// built-in strategy for its delays, without waiting, so you can compare them side by side and pick the right one.
//
//   dotnet run Examples/strategies.cs

using System.Net;
using System.Net.Http;

using Spectre.Console;

using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Strategies").Color(Color.MediumPurple).Centered());
AnsiConsole.Write(new Rule("[mediumpurple]every strategy's delay sequence, side by side[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "Retry too fast and you hammer a service that is already struggling; too slow and the caller waits for " +
        "nothing.\nThe strategy classes count [white]tries[/]: [white]new Count(3)[/] makes 3 calls. The " +
        "[white]RetryStrategies[/] factories count [white]retries[/]: [white]Constant(retries: 3, ...)[/] makes 4.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

static TimeSpan ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
static TimeSpan s(int seconds) => TimeSpan.FromSeconds(seconds);

// RetryAfter reads the Retry-After header of an HttpResponseMessage result. The simulated service answers attempts
// 1, 2 and 3 with a 503 that asks for 3 s, a 429 that asks for 6 s, and a 503 with no header.
HttpResponseMessage[] responses = [Response(HttpStatusCode.ServiceUnavailable, s(3)), Response(HttpStatusCode.TooManyRequests, s(6)), Response(HttpStatusCode.ServiceUnavailable, null)];

StrategyRow[] rows =
[
    new("[mediumpurple]Classes: maxTries[/]", null),
    new("new Count(3)", new Count(3)),
    new("new DoNotRetry()", new DoNotRetry()),
    new("new Linear(500ms, 4)", new Linear(ms(500), maxTries: 4)),
    new("new Incremental(5, 1s, 2s)", new Incremental(5, s(1), s(2))),
    new("new Backoff(5, 1s)", new Backoff(5, s(1)), Random: true),
    new("  { MaxBackoff = 10s }", new Backoff(5, s(1)) { MaxBackoff = s(10) }, Random: true),
    new("new Exponential(6, 1s)", new Exponential(6, s(1))),

    // maxDelay caps the exponential growth, so the fifth retry waits 5 s rather than 16 s.
    new("  maxDelay: 5s", new Exponential(6, s(1), maxDelay: s(5))),
    new("  useJitter: true", new Exponential(6, s(1), useJitter: true), Random: true),
    new("  both", new Exponential(6, s(1), maxDelay: s(5), useJitter: true), Random: true),
    new("[mediumpurple]RetryStrategies: retries[/]", null),
    new("Constant(3, 200ms)", RetryStrategies.Constant(retries: 3, delay: ms(200))),
    new("Linear(5, 500ms)", RetryStrategies.Linear(retries: 5, delay: ms(500))),
    new("Exponential(5, 1s)", RetryStrategies.Exponential(retries: 5, baseDelay: s(1))),
    new("FromDelegate(100ms × n)", RetryStrategies.FromDelegate(a => a.AttemptNumber <= 3 ? ms(100 * a.AttemptNumber) : null)),
    new("RetryAfter(Constant(3, 1s))", RetryStrategies.RetryAfter(RetryStrategies.Constant(retries: 3, delay: s(1))), Results: responses),
];

const int Columns = 6;
var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn(new TableColumn("Strategy").NoWrap()).AddColumn(new TableColumn("Calls").Centered());
for (var retry = 1; retry <= Columns; retry++)
{
    table.AddColumn(new TableColumn($"R{retry}").RightAligned().Padding(1, 0, 0, 0));
}

foreach (var row in rows)
{
    if (row.Strategy is null)
    {
        table.AddRow(row.Name);
        continue;
    }

    var delays = Sequence(row.Strategy, row.Results);
    var cells = new List<string> { Markup.Escape(row.Name) + (row.Random ? " [grey]*[/]" : ""), $"[white]{delays.Count + 1}[/]" };
    cells.AddRange(delays.Take(Columns).Select(Format));
    if (cells.Count < Columns + 2)
    {
        cells.Add("[red]stop[/]");
    }

    table.AddRow(cells.ToArray());
}

AnsiConsole.Write(table);
AnsiConsole.MarkupLine("[grey]  Rn is the wait after attempt n fails. * random: differs on every run.[/]");
AnsiConsole.MarkupLine("[grey]  Tries versus retries: Count(3) makes 3 calls; Constant(retries: 3) makes 4.[/]");
AnsiConsole.MarkupLine("[grey]  maxDelay holds Exponential at 5 s; uncapped jitter can triple each delay.[/]");
AnsiConsole.MarkupLine("[grey]  RetryAfter obeys the 3 s and 6 s headers, then falls back to the inner 1 s.[/]");
AnsiConsole.WriteLine();

// Decorrelated jitter picks each delay between the base delay and three times the previous one, so it needs each
// returned delay fed back as the next RetryAttempt.PreviousDelay, exactly as the library does during a call.
AnsiConsole.Write(new Rule("[mediumpurple]RetryStrategies.Exponential(retries: 5, baseDelay: 1 s, maxDelay: 20 s)[/]").LeftJustified());
var plain = Sequence(RetryStrategies.Exponential(retries: 5, baseDelay: s(1), maxDelay: s(20)));
var jittered = Sequence(RetryStrategies.Exponential(retries: 5, baseDelay: s(1), maxDelay: s(20), useJitter: true));
var chart = new BarChart().Width(70).Label("[white]delay before each retry, in seconds[/]");
for (var i = 0; i < plain.Count; i++)
{
    chart.AddItem($"retry {i + 1}", Math.Round(plain[i].TotalSeconds, 1), Color.Grey);
    chart.AddItem($"  jitter", Math.Round(jittered[i].TotalSeconds, 1), Color.MediumPurple);
}

AnsiConsole.Write(chart);
AnsiConsole.MarkupLine($"[grey]  Total wait: {plain.Sum(d => d.TotalSeconds):0.0} s plain, {jittered.Sum(d => d.TotalSeconds):0.0} s with jitter (random: run again for another).[/]");

// Asks the strategy for each delay in turn, as the library would after each failed attempt, until it says stop.
static List<TimeSpan> Sequence(IRetryStrategy strategy, HttpResponseMessage[]? results = null)
{
    var delays = new List<TimeSpan>();
    var previous = TimeSpan.Zero;
    var failure = new HttpRequestException("simulated failure");
    for (var attempt = 1; attempt <= 20; attempt++)
    {
        object? result = results is not null && attempt <= results.Length ? results[attempt - 1] : null;
        if (strategy.GetRetryDelay(new RetryAttempt(attempt, result is null ? failure : null, result, previous)) is not TimeSpan delay)
        {
            break;
        }

        delays.Add(delay);
        previous = delay;
    }

    return delays;
}

static string Format(TimeSpan delay) => delay == TimeSpan.Zero
    ? "0"
    : delay < TimeSpan.FromSeconds(1) ? $"{delay.TotalMilliseconds:0}ms" : $"{delay.TotalSeconds:0.0}s";

static HttpResponseMessage Response(HttpStatusCode status, TimeSpan? retryAfter)
{
    var response = new HttpResponseMessage(status);
    if (retryAfter is TimeSpan delta)
    {
        response.Headers.RetryAfter = new(delta);
    }

    return response;
}

record StrategyRow(string Name, IRetryStrategy? Strategy, bool Random = false, HttpResponseMessage[]? Results = null);
