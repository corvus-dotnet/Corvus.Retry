#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Waiting for a dependency that is still starting, as when an app container starts before its database or message
// broker. The driver wraps the socket error in its own exception, so the policy must look inside it; and an error that
// waiting cannot fix, such as a wrong password, must fail at once instead of retrying for a minute.
//
//   dotnet run Examples/database-startup.cs

using System.Diagnostics;
using System.Net.Sockets;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Database").Color(Color.SpringGreen3).Centered());
AnsiConsole.Write(new Rule("[springgreen3]wait for a dependency that is still starting[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "The app starts before the database is ready. The first [white]5[/] connection attempts fail with the driver's " +
        "[white]InvalidOperationException[/], which wraps a [white]SocketException[/] (connection refused). The app must " +
        "wait with backoff and then connect, but a [white]wrong password[/] will not fix itself, so that must fail at once.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// Up to 8 retries, from 100 ms; maxDelay caps the growth so that the app checks again at least every 800 ms, and
// jitter stops a fleet of containers that started together from all knocking at the same moment.
var strategy = RetryStrategies.Exponential(retries: 8, baseDelay: TimeSpan.FromMilliseconds(100), maxDelay: TimeSpan.FromMilliseconds(800), useJitter: true);

// The driver throws its own exception type for every error. The socket error is inside it, so Handle<SocketException>
// never sees it; HandleInner looks through the InnerException chain.
var policy = RetryPolicies.HandleInner<SocketException>();

// 1. Which policy matches what the driver throws?
var sample = StartingDatabase.Refused("db:5432");
var policies = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .Title("[springgreen3]The driver throws InvalidOperationException, wrapping a SocketException[/]")
    .AddColumn("Policy").AddColumn("CanRetry").AddColumn("Why");
policies.AddRow("Handle<SocketException>()", Verdict(RetryPolicies.Handle<SocketException>().CanRetry(sample)), "it examines only the outer exception");
policies.AddRow("HandleInner<SocketException>()", Verdict(policy.CanRetry(sample)), "it also examines the InnerException chain");
AnsiConsole.Write(policies);
AnsiConsole.WriteLine();

// 2. The database refuses five connections, and then accepts one.
var attempts = new List<(int Attempt, string Error, TimeSpan Delay, long AtMs)>();
var database = new StartingDatabase(refusals: 5);
var clock = Stopwatch.StartNew();

var connection = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("connecting to db:5432", ctx =>
    Retriable.RetryAsync(
        ct => database.OpenAsync(ct),
        CancellationToken.None,
        strategy,
        policy,
        continueOnCapturedContext: false,
        onRetry: args =>
        {
            attempts.Add((args.AttemptNumber, Describe(args.LastException), args.DelayBeforeRetry, clock.ElapsedMilliseconds));
            ctx.Status($"attempt {args.AttemptNumber} failed, retrying in {args.DelayBeforeRetry.TotalMilliseconds:F0} ms");
            return ValueTask.CompletedTask;
        }));

var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[springgreen3]Connecting while the database starts[/]")
    .AddColumn("#").AddColumn("Driver threw").AddColumn(new TableColumn("Failed at").RightAligned()).AddColumn(new TableColumn("Wait").RightAligned());
foreach (var (attempt, error, delay, atMs) in attempts)
{
    table.AddRow(attempt.ToString(), error, $"{atMs} ms", $"{delay.TotalMilliseconds:F0} ms");
}

table.AddRow((attempts.Count + 1).ToString(), $"[green]connected to {connection.Host}[/]", $"{clock.ElapsedMilliseconds} ms", "-");
AnsiConsole.Write(table);
var waited = TimeSpan.FromTicks(attempts.Sum(a => a.Delay.Ticks));
AnsiConsole.MarkupLine($"  Waited [springgreen3]{waited.TotalMilliseconds:F0} ms[/] in total, over [springgreen3]{attempts.Count}[/] retries; the other {8 - attempts.Count} retries were not needed.");
AnsiConsole.WriteLine();

// 3. A wrong password: the same wrapper exception, but no SocketException inside, so the policy refuses at once.
var misconfigured = new StartingDatabase(refusals: 0, wrongPassword: true);
clock.Restart();
try
{
    await Retriable.RetryAsync(ct => misconfigured.OpenAsync(ct), CancellationToken.None, strategy, policy);
}
catch (InvalidOperationException ex)
{
    AnsiConsole.Write(new Panel(
            $"[red]{Markup.Escape(ex.GetType().Name)}[/]: {Markup.Escape(ex.Message)}\n" +
            $"Inner exception: [white]{Markup.Escape(ex.InnerException?.GetType().Name ?? "none")}[/]\n" +
            $"Attempts: [white]{misconfigured.Attempts}[/], time: [white]{clock.ElapsedMilliseconds} ms[/]. The policy refused, so no retry.")
        .Header("[red]Wrong password: fail fast[/]").Border(BoxBorder.Rounded).BorderColor(Color.Red));
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  Jitter makes each wait random: 100 ms to 3 times the last wait, at most 800 ms.[/]");
AnsiConsole.MarkupLine("[grey]  AnyExceptionPolicy would retry the wrong password 8 times before it gave up.[/]");

static string Verdict(bool canRetry) => canRetry ? "[green]true[/]" : "[red]false[/]";

static string Describe(Exception? ex) =>
    ex?.InnerException is SocketException socket
        ? $"{ex.GetType().Name} ({socket.SocketErrorCode})"
        : Markup.Escape(ex?.GetType().Name ?? "?");

// A connection to the simulated database.
internal sealed record Connection(string Host);

// A database that refuses connections while it starts, as a driver reports it: the driver's own exception, with the
// socket error as its InnerException.
internal sealed class StartingDatabase(int refusals, bool wrongPassword = false)
{
    public int Attempts { get; private set; }

    public static InvalidOperationException Refused(string host) =>
        new($"Failed to connect to {host}", new SocketException((int)SocketError.ConnectionRefused));

    public async Task<Connection> OpenAsync(CancellationToken cancellationToken)
    {
        this.Attempts++;
        await Task.Delay(20, cancellationToken); // The network round trip.

        if (wrongPassword)
        {
            throw new InvalidOperationException("28P01: password authentication failed for user \"app\"");
        }

        if (this.Attempts <= refusals)
        {
            throw Refused("db:5432");
        }

        return new Connection("db:5432");
    }
}
