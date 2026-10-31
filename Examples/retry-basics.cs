#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Retriable puts retries around a method call: if the call throws, Retriable calls it again. This demo runs it against
// an in-process flaky service to show the defaults, a strategy with a delay and an onRetry callback, cancellation, and
// what happens when the service never recovers.
//
//   dotnet run Examples/retry-basics.cs

using System.Diagnostics;
using System.Net.Http;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Retry basics").Color(Color.Aqua).Centered());
AnsiConsole.Write(new Rule("[aqua]Retriable.Retry and Retriable.RetryAsync against a flaky service[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A service drops the first [white]3[/] calls and then answers. Without retries, the caller sees an error " +
        "for a fault that clears in milliseconds.\nWith [white]Retriable[/], one line turns that into a result, and " +
        "the strategy, the policy, a callback and a cancellation token control how hard it tries.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// 1. The defaults: RetryStrategies.Default (a shared Count(10): ten tries, no delay) and AnyExceptionPolicy.
AnsiConsole.Write(new Rule("[aqua]1. The defaults[/]").LeftJustified());
var syncService = new FlakyService("inventory", failuresBeforeSuccess: 3);
var reply = Retriable.Retry(() => syncService.Ping());
AnsiConsole.MarkupLine($"  [grey]Retry:[/]      [white]{Markup.Escape(reply)}[/] after [white]{syncService.Calls}[/] calls");

var asyncService = new FlakyService("pricing", failuresBeforeSuccess: 3);
var asyncReply = await Retriable.RetryAsync(() => asyncService.PingAsync());
AnsiConsole.MarkupLine($"  [grey]RetryAsync:[/] [white]{Markup.Escape(asyncReply)}[/] after [white]{asyncService.Calls}[/] calls");
AnsiConsole.MarkupLine("[grey]  Ten tries and no delay by default: three failures cost nothing visible.[/]");
AnsiConsole.WriteLine();

// 2. A strategy with a delay, and onRetry to watch each retry. RetryStrategies.Constant counts retries, not tries:
// retries: 3 allows four calls. A 200 ms pause gives the service time to recover instead of hammering it.
AnsiConsole.Write(new Rule("[aqua]2. RetryStrategies.Constant(retries: 3, delay: 200 ms) with onRetry[/]").LeftJustified());
var attempts = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("Attempt").AddColumn("Outcome").AddColumn("Next attempt").AddColumn(new TableColumn("Delay").RightAligned())
    .AddColumn(new TableColumn("Elapsed").RightAligned());
var constantService = new FlakyService("orders", failuresBeforeSuccess: 3);
var stopwatch = Stopwatch.StartNew();
string? constantReply = null;

await ShowLiveAsync(attempts, async refresh =>
{
    constantReply = await Retriable.RetryAsync(
        ct => constantService.PingAsync(ct),
        CancellationToken.None,
        RetryStrategies.Constant(retries: 3, delay: TimeSpan.FromMilliseconds(200)),
        AnyExceptionPolicy.Default,
        continueOnCapturedContext: false,
        onRetry: args =>
        {
            // Retriable awaits the callback before each delay; it is not called for the last failure.
            attempts.AddRow(
                $"{args.AttemptNumber}",
                $"[red]{Markup.Escape(args.LastException?.Message ?? "")}[/]",
                $"{args.NextAttemptNumber}",
                $"{args.DelayBeforeRetry.TotalMilliseconds:0} ms",
                $"{stopwatch.ElapsedMilliseconds} ms");
            refresh();
            return ValueTask.CompletedTask;
        });
    attempts.AddRow($"{constantService.Calls}", $"[green]{Markup.Escape(constantReply)}[/]", "-", "-", $"{stopwatch.ElapsedMilliseconds} ms");
    refresh();
});
AnsiConsole.MarkupLine("[grey]  retries: 3 allows four calls; the fourth succeeds after three 200 ms pauses.[/]");
AnsiConsole.WriteLine();

// 3. Cancellation. The token is examined before each attempt and stops a delay immediately, so the caller gets
// control back as soon as it asks, not after the strategy has run out.
AnsiConsole.Write(new Rule("[aqua]3. A cancellation token stops the retries[/]").LeftJustified());
var downService = new FlakyService("billing", failuresBeforeSuccess: int.MaxValue);
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
{
    stopwatch.Restart();
    try
    {
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Retrying every 200 ms; the token fires at 700 ms...", _ =>
            Retriable.RetryAsync(
                ct => downService.PingAsync(ct),
                cancellation.Token,
                RetryStrategies.Constant(retries: 20, delay: TimeSpan.FromMilliseconds(200)),
                AnyExceptionPolicy.Default));
    }
    catch (OperationCanceledException ex)
    {
        AnsiConsole.MarkupLine($"  [white]{ex.GetType().Name}[/] (an OperationCanceledException) after [white]{downService.Calls}[/] of 21 calls,");
        AnsiConsole.MarkupLine($"  at {stopwatch.ElapsedMilliseconds} ms, not after the 4 s that 20 retries would take");
        AnsiConsole.MarkupLine($"  [grey]Carries the caller's token:[/] [white]{ex.CancellationToken == cancellation.Token}[/]");
    }
}

AnsiConsole.MarkupLine("[grey]  No policy can retry a cancellation that the caller's token caused.[/]");
AnsiConsole.WriteLine();

// 4. A service that never recovers. When the strategy runs out, Retriable rethrows the last exception itself, not a
// wrapper, so existing catch blocks and stack traces keep working.
AnsiConsole.Write(new Rule("[aqua]4. The service never recovers[/]").LeftJustified());
var deadService = new FlakyService("search", failuresBeforeSuccess: int.MaxValue);
try
{
    await Retriable.RetryAsync(
        ct => deadService.PingAsync(ct),
        CancellationToken.None,
        RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(100)),
        AnyExceptionPolicy.Default);
}
catch (Exception ex)
{
    AnsiConsole.Write(new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).HideHeaders()
        .AddColumn("").AddColumn("")
        .AddRow("[grey]Calls made[/]", $"{deadService.Calls}")
        .AddRow("[grey]Type[/]", $"[red]{ex.GetType().Name}[/]")
        .AddRow("[grey]Message[/]", Markup.Escape(ex.Message))
        .AddRow("[grey]Inner exception[/]", ex.InnerException is null ? "none" : Markup.Escape(ex.InnerException.GetType().Name))
        .AddRow("[grey]Same object the last call threw[/]", $"[white]{ReferenceEquals(ex, deadService.LastThrown)}[/]"));
}

AnsiConsole.MarkupLine("[grey]  Retriable rethrows the last failure itself, so catch what the service throws.[/]");

// Live redraws the table in place on a terminal; when the output is redirected it cannot move the cursor, so the
// table is written once at the end instead.
static async Task ShowLiveAsync(Table table, Func<Action, Task> body)
{
    if (AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected)
    {
        await AnsiConsole.Live(table).StartAsync(context => body(context.Refresh));
    }
    else
    {
        await body(() => { });
        AnsiConsole.Write(table);
    }
}

// A service that fails a fixed number of times and then answers. int.MaxValue failures means it never recovers.
sealed class FlakyService(string name, int failuresBeforeSuccess)
{
    public int Calls { get; private set; }

    public Exception? LastThrown { get; private set; }

    public string Ping()
    {
        this.Calls++;
        if (this.Calls <= failuresBeforeSuccess)
        {
            this.LastThrown = new HttpRequestException($"connection reset (call {this.Calls})");
            throw this.LastThrown;
        }

        return $"pong from {name} on call {this.Calls}";
    }

    public async Task<string> PingAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken);
        return this.Ping();
    }
}
