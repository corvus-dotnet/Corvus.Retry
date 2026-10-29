#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// One composed ReliabilityPipeline around a real HttpClient call: retries for transient statuses and exceptions, the
// Retry-After header, a time limit for each attempt, a shared circuit breaker and a cached fallback. The service is a
// scripted HttpMessageHandler, so the demo needs no network, and the handler is small enough to copy into your tests.
//
//   dotnet run Examples/http-client-resilience.cs

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("HTTP Client").Color(Color.DeepSkyBlue1).Centered());
AnsiConsole.Write(new Rule("[deepskyblue1]one pipeline for every way an HTTP call can fail[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "The catalogue service answers [white]503[/], hangs for [white]2 s[/], answers [white]429 with Retry-After: 1[/], " +
        "answers [white]500[/], and then [white]200[/]. Each attempt gets [white]500 ms[/]. One pipeline retries up to " +
        "[white]5[/] times, obeys the server's header, and keeps a circuit breaker and a cached answer for the day the " +
        "service stays down.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var log = new List<Row>();
var circuitChanges = new List<string>();
var clock = new Stopwatch();
var fellBack = false;
StatusContext? spinner = null;

// One breaker for the service, shared by every request. It opens when three calls in four fail, but only after it
// has seen eight calls, so that one bad request does not cut the service off.
var breaker = new CircuitBreaker(new CircuitBreakerOptions
{
    FailureRatio = 0.75,
    MinimumThroughput = 8,
    BreakDuration = TimeSpan.FromSeconds(5),
    OnStateChanged = change =>
    {
        circuitChanges.Add($"{change.From} -> {change.To} at {clock.ElapsedMilliseconds} ms into run 2");
        return ValueTask.CompletedTask;
    },
});

// Build the pipeline once and keep it: it is immutable, and many requests can use it at the same time.
var pipeline = new ReliabilityPipeline<HttpResponseMessage>(new ReliabilityPipelineOptions<HttpResponseMessage>
{
    // Exceptions: an HttpRequestException with a transient status (or none), and an attempt timeout. It refuses a
    // BrokenCircuitException, so an open circuit goes straight to the fallback instead of waiting for the break.
    Policy = RetryPolicies.TransientHttp,

    // HttpClient returns 408, 429 and 5xx as responses, not exceptions, so the pipeline must examine results too.
    ShouldRetryResult = RetryPolicies.IsTransientHttpResponse,

    // The server's Retry-After header wins when it sends one. Otherwise exponential backoff with jitter, so that many
    // clients do not retry in step; maxDelay caps the growth so that the fifth retry does not wait 3.2 s or more.
    Strategy = RetryStrategies.RetryAfter(
        RetryStrategies.Exponential(retries: 5, baseDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromSeconds(2), useJitter: true)),

    // A hung connection costs 500 ms, not the 100 s default of HttpClient.Timeout.
    AttemptTimeout = TimeSpan.FromMilliseconds(500),
    CircuitBreaker = breaker,

    // The pipeline awaits this before each delay, while the rejected response is still readable.
    OnRetry = args =>
    {
        log.Add(Row.ForRetry(args, clock.ElapsedMilliseconds));
        spinner?.Status($"attempt {args.AttemptNumber} failed, retrying in {args.DelayBeforeRetry.TotalMilliseconds:F0} ms");
        return ValueTask.CompletedTask;
    },

    // When the retries run out the pipeline returns the last response, so a final 503 must also get the cached answer.
    Fallback = new FallbackOptions<HttpResponseMessage>
    {
        ShouldFallbackResult = RetryPolicies.IsTransientHttpResponse,
        OnFallback = args =>
        {
            fellBack = true;
            log.Add(Row.ForLast(log.Count + 1, args.Result, args.Exception, clock.ElapsedMilliseconds, fellBack: true));
            return ValueTask.CompletedTask;
        },
        Action = _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "id": 42, "name": "Raven feeder", "stock": null, "cached": "10 min ago" }"""),
        }),
    },
});

await SendAsync("Run 1: the service recovers", new ScriptedHandler(
    Step.Status(HttpStatusCode.ServiceUnavailable),
    Step.Hang(TimeSpan.FromSeconds(2)),
    Step.TooManyRequests(retryAfter: TimeSpan.FromSeconds(1)),
    Step.Status(HttpStatusCode.InternalServerError),
    Step.Json("""{ "id": 42, "name": "Raven feeder", "stock": 7 }""")));

// The last step of a script repeats, so this service never recovers.
await SendAsync("Run 2: the service stays down", new ScriptedHandler(Step.Status(HttpStatusCode.ServiceUnavailable)));

AnsiConsole.Write(new Rule("[deepskyblue1]Circuit breaker[/]").LeftJustified());
foreach (var change in circuitChanges)
{
    AnsiConsole.MarkupLine($"  [orange1]{Markup.Escape(change)}[/]");
}

AnsiConsole.MarkupLine($"  The circuit is now [orange1]{breaker.State}[/]: the next request gets the cache at once.");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  The strategy's delays are random (decorrelated jitter); Retry-After is exact.[/]");
AnsiConsole.MarkupLine("[grey]  The breaker also counted run 1, so it opened on the 8th call, during run 2.[/]");

// Sends one request through the pipeline, then renders the request log and the body that the caller received.
async Task SendAsync(string title, ScriptedHandler handler)
{
    log.Clear();
    fellBack = false;
    clock.Restart();
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://catalogue.example/") };

    using var response = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(
        "GET /products/42",
        async ctx =>
        {
            spinner = ctx;
            return await pipeline.ExecuteAsync(ct => new ValueTask<HttpResponseMessage>(client.GetAsync("products/42", ct)));
        });

    if (!fellBack)
    {
        log.Add(Row.ForLast(log.Count + 1, response, null, clock.ElapsedMilliseconds, fellBack: false));
    }

    var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[deepskyblue1]{title}[/]")
        .AddColumn("#").AddColumn("Handler did").AddColumn("Outcome").AddColumn(new TableColumn("Ended at").RightAligned())
        .AddColumn(new TableColumn("Delay").RightAligned()).AddColumn("Why");
    foreach (var row in log)
    {
        var did = row.Attempt <= handler.Calls.Count ? handler.Calls[row.Attempt - 1] : "[grey]not called[/]";
        table.AddRow(row.Attempt.ToString(), did, row.Outcome, $"{row.AtMs} ms", row.Delay, row.Why);
    }

    AnsiConsole.Write(table);
    var body = await response.Content.ReadAsStringAsync();
    AnsiConsole.Write(new Panel(Markup.Escape(body))
        .Header(fellBack ? "[orange1]Body (from the fallback)[/]" : "[green]Body (from the service)[/]")
        .Border(BoxBorder.Rounded).BorderColor(fellBack ? Color.Orange1 : Color.Green));
    AnsiConsole.WriteLine();
}

// One line of the request log.
internal sealed record Row(int Attempt, string Outcome, long AtMs, string Delay, string Why)
{
    public static Row ForRetry(RetryEventArgs args, long atMs)
    {
        var response = args.Result as HttpResponseMessage;
        var why = response?.Headers.RetryAfter is not null ? "[white]Retry-After[/]" : "strategy";
        return new(args.AttemptNumber, Describe(response, args.LastException), atMs, $"{args.DelayBeforeRetry.TotalMilliseconds:F0} ms", why);
    }

    public static Row ForLast(int attempt, HttpResponseMessage? response, Exception? exception, long atMs, bool fellBack) =>
        new(attempt, Describe(response, exception), atMs, "-", fellBack ? "[orange1]fallback[/]" : "[green]done[/]");

    private static string Describe(HttpResponseMessage? response, Exception? exception) => (response, exception) switch
    {
        ({ IsSuccessStatusCode: true } r, _) => $"[green]{(int)r.StatusCode} {r.ReasonPhrase}[/]",
        ({ } r, _) => $"[red]{(int)r.StatusCode} {r.ReasonPhrase}[/]",
        (_, RetryTimeoutException t) => $"[red]timed out after {t.Timeout.TotalMilliseconds:F0} ms[/]",
        (_, BrokenCircuitException) => "[orange1]rejected: circuit open[/]",
        (_, { } e) => $"[red]{Markup.Escape(e.GetType().Name)}[/]",
        _ => "?",
    };
}

// One scripted answer: a description for the log, and the response (or the hang) that the handler gives.
internal sealed record Step(string Description, Func<CancellationToken, Task<HttpResponseMessage>> Respond)
{
    public static Step Status(HttpStatusCode code) =>
        new($"answered {(int)code}", _ => Task.FromResult(new HttpResponseMessage(code)));

    public static Step TooManyRequests(TimeSpan retryAfter) =>
        new($"answered 429, Retry-After: {retryAfter.TotalSeconds:F0}", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
        }));

    public static Step Json(string json) =>
        new("answered 200 with JSON", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

    // Waits without answering. The pipeline's attempt timeout cancels the token, which ends the wait.
    public static Step Hang(TimeSpan duration) =>
        new($"hung for {duration.TotalSeconds:F0} s", async ct =>
        {
            await Task.Delay(duration, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
}

// An HttpMessageHandler that plays a script, one step for each request. The last step repeats.
internal sealed class ScriptedHandler(params Step[] steps) : HttpMessageHandler
{
    public List<string> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var step = steps[Math.Min(this.Calls.Count, steps.Length - 1)];
        this.Calls.Add(step.Description);
        return step.Respond(cancellationToken);
    }
}
