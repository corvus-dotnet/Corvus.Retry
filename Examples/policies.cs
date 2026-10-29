#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// A policy decides which exceptions may cause a retry: transient faults (a 503, a dropped socket) are worth another
// try, permanent ones (a 404, bad input) are not. This demo runs every built-in policy and combinator against the same
// exceptions through Retriable, then shows a policy stopping a live run early when the fault turns permanent.
//
//   dotnet run Examples/policies.cs

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Policies").Color(Color.Orange1).Centered());
AnsiConsole.Write(new Rule("[orange1]which exceptions are worth another try?[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "Retrying a [white]503[/] gives a busy service time to recover. Retrying a [white]404[/] or bad input only " +
        "repeats the same failure, and adds load and latency.\nA policy looks at the exception and says yes or " +
        "no. The strategy then decides how long to wait and how many times.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// The exceptions under test. The caller's cancellation and the aggregate are built per call by Throw, below.
(string Code, string Description)[] exceptions =
[
    ("503", "HttpRequestException, status 503 Service Unavailable"),
    ("429", "HttpRequestException, status 429 Too Many Requests"),
    ("404", "HttpRequestException, status 404 Not Found"),
    ("TCE", "TaskCanceledException (how HttpClient reports a timeout)"),
    ("TO", "TimeoutException"),
    ("Sock", "IOException wrapping a SocketException"),
    ("Agg", "AggregateException holding a 503 HttpRequestException"),
    ("OCE", "OperationCanceledException from the caller's own token"),
];

var legend = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("Code").AddColumn("Exception thrown");
foreach (var (code, description) in exceptions)
{
    legend.AddRow($"[orange1]{code}[/]", Markup.Escape(description));
}

AnsiConsole.Write(legend);
AnsiConsole.WriteLine();

// Retry a server error (5xx) but not a client error (4xx): the status code says whether the fault is ours.
var serverErrors = RetryPolicies.FromPredicate(ex => ex is HttpRequestException { StatusCode: HttpStatusCode code } && (int)code >= 500);

(string Name, IRetryPolicy Policy)[] policies =
[
    ("AnyExceptionPolicy.Default", AnyExceptionPolicy.Default),
    ("new DoNotRetryPolicy()", new DoNotRetryPolicy()),
    ("Handle<HttpRequestException>", RetryPolicies.Handle<HttpRequestException>()),
    ("  .Or<TimeoutException>()", RetryPolicies.Handle<HttpRequestException>().Or<TimeoutException>()),
    ("HandleInner<SocketException>", RetryPolicies.HandleInner<SocketException>()),
    ("FromPredicate(5xx)", serverErrors),

    // TransientHttp accepts a RetryTimeoutException (an attempt timeout of the pipeline), but not a TaskCanceledException
    // or a plain TimeoutException, which can come from anywhere.
    ("TransientHttp", RetryPolicies.TransientHttp),
    ("Not(TransientHttp)", RetryPolicies.Not(RetryPolicies.TransientHttp)),
    ("Any(TransientHttp, Socket)", RetryPolicies.Any(RetryPolicies.TransientHttp, RetryPolicies.HandleInner<SocketException>())),

    // Retry transient HTTP faults, except rate limiting, which this caller prefers to report at once.
    ("All(TransientHttp, Not 429)", RetryPolicies.All(RetryPolicies.TransientHttp, new TooManyRequestsPolicy().Not())),

    // AggregatePolicy is the mutable form of All: you can add child policies after you make it.
    ("AggregatePolicy, same pair", new AggregatePolicy { Policies = { RetryPolicies.TransientHttp, new TooManyRequestsPolicy().Not() } }),
    ("new TooManyRequestsPolicy()", new TooManyRequestsPolicy()),
];

var matrix = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn(new TableColumn("Policy").NoWrap());
foreach (var (code, _) in exceptions)
{
    matrix.AddColumn(new TableColumn(code).Centered().Padding(1, 0, 0, 0));
}

foreach (var (name, policy) in policies)
{
    var cells = new List<string> { Markup.Escape(name) };
    foreach (var (code, _) in exceptions)
    {
        cells.Add(Retries(policy, code) ? "[green]✓[/]" : "[red]✗[/]");
    }

    matrix.AddRow(cells.ToArray());
}

AnsiConsole.Write(matrix);
AnsiConsole.MarkupLine("[grey]  Each cell is a real Retriable.Retry call: ✓ means the operation ran twice.[/]");
AnsiConsole.MarkupLine("[grey]  Agg: Retriable asks the policy about each inner exception, not the wrapper.[/]");
AnsiConsole.MarkupLine("[grey]  OCE: no policy is asked about a cancellation that the caller's token caused.[/]");
AnsiConsole.WriteLine();

// The live run: a service that is overloaded (503) for two calls and then reports a permanent 404. The strategy allows
// five retries, but serverErrors refuses the 404, so Retriable stops at once and rethrows it.
AnsiConsole.Write(new Rule("[orange1]Live: FromPredicate(5xx) with Constant(retries: 5, 150 ms)[/]").LeftJustified());
var attempts = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("Attempt").AddColumn("Service said").AddColumn("Policy").AddColumn(new TableColumn("Next").RightAligned());
var calls = 0;
await ShowLiveAsync(attempts, async refresh =>
{
    try
    {
        await Retriable.RetryAsync(
            async () =>
            {
                calls++;
                await Task.Delay(20);
                throw new HttpRequestException("lookup failed", null, calls <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
            },
            CancellationToken.None,
            RetryStrategies.Constant(retries: 5, delay: TimeSpan.FromMilliseconds(150)),
            serverErrors,
            continueOnCapturedContext: false,
            onRetry: args =>
            {
                attempts.AddRow($"{args.AttemptNumber}", Status(args.LastException), "[green]retry[/]", $"{args.DelayBeforeRetry.TotalMilliseconds:0} ms");
                refresh();
                return ValueTask.CompletedTask;
            });
    }
    catch (HttpRequestException ex)
    {
        attempts.AddRow($"{calls}", Status(ex), "[red]refuse[/]", "rethrown");
        refresh();
    }
});

AnsiConsole.MarkupLine($"[grey]  {calls} calls of the 6 permitted: the 404 is permanent, so the retries stop early.[/]");

// Runs one call through Retriable with a strategy that allows two tries, and reports whether the second try ran.
static bool Retries(IRetryPolicy policy, string code)
{
    using var caller = new CancellationTokenSource();
    var calls = 0;
    try
    {
        Retriable.Retry(() => { calls++; Throw(code, caller); }, caller.Token, new Count(2), policy);
    }
    catch (Exception)
    {
        // Every simulated call throws, so Retriable always gives up in the end; only the number of calls matters.
    }

    return calls == 2;
}

static void Throw(string code, CancellationTokenSource caller)
{
    switch (code)
    {
        case "503": throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable);
        case "429": throw new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests);
        case "404": throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
        case "TCE": throw new TaskCanceledException("HttpClient.Timeout elapsed");
        case "TO": throw new TimeoutException();
        case "Sock": throw new IOException("read failed", new SocketException((int)SocketError.ConnectionReset));
        case "Agg": throw new AggregateException(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable));
        default:
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
    }
}

static string Status(Exception? ex) =>
    ex is HttpRequestException { StatusCode: HttpStatusCode code } ? $"{(int)code} {code}" : ex?.GetType().Name ?? "";

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

// A custom policy: retry only when the service asks the caller to slow down. It keeps no state for a call, because
// many calls can use one policy at the same time.
sealed class TooManyRequestsPolicy : IRetryPolicy
{
    public bool CanRetry(Exception exception) => exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
}
