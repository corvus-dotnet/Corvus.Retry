#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Shows ReliabilityPipeline<HttpResponseMessage> retrying responses, not exceptions. IsTransientHttpResponse rejects
// a 503 and a 429, RetryAfter waits as long as the server asks, and the pipeline disposes each rejected response after
// OnRetry has seen it, so that a retry loop does not leak connections.
//
//   dotnet run Examples/result-retries.cs

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Results").Color(Color.DeepSkyBlue1).Centered());
AnsiConsole.Write(new Rule("[deepskyblue1]retry a 503 or a 429 response, honour Retry-After, dispose what you reject[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A busy HTTP API does not throw: it answers [white]503[/], or [white]429 Too Many Requests[/] with " +
        "[white]Retry-After: 1[/]. HttpClient returns both as ordinary responses, so a policy for exceptions never sees " +
        "them. Here the server answers [white]503 → 429 → 200[/].")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var handler = new ScriptedHandler(
    (HttpStatusCode.ServiceUnavailable, null),
    (HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1)),
    (HttpStatusCode.OK, null));
using var client = new HttpClient(handler) { BaseAddress = new Uri("https://catalogue.example/") };

var retries = new Dictionary<int, RetryObservation>();
var pipeline = new ReliabilityPipeline<HttpResponseMessage>(new ReliabilityPipelineOptions<HttpResponseMessage>
{
    // IsTransientHttpResponse is true for 408, 429 and 5xx, so the pipeline treats those responses as failures.
    ShouldRetryResult = RetryPolicies.IsTransientHttpResponse,

    // RetryAfter takes the delay from the Retry-After header when there is one, and from the inner strategy when
    // there is not. The 100 ms base keeps the demo short; the 429 overrides it with the full second the server asked for.
    Strategy = RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 4, baseDelay: TimeSpan.FromMilliseconds(100))),

    // Exceptions, such as a dropped connection, still go to the policy. This script has none.
    Policy = RetryPolicies.TransientHttp,

    OnRetry = args =>
    {
        // For a result-caused retry, Result holds the rejected response and LastException is null. The response is
        // still alive here: the pipeline disposes it only after this callback, so you can log its body.
        var response = (TrackedResponse)args.Result!;
        retries[args.AttemptNumber] = new(
            args.LastException is null ? "Result" : "LastException",
            args.DelayBeforeRetry,
            response.IsDisposed);
        return ValueTask.CompletedTask;
    },
});

var clock = Stopwatch.StartNew();
var final = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(
    "Calling GET /products/KT-100 (the 429 asks for a 1 s wait)…",
    _ => pipeline.ExecuteAsync(ct => new ValueTask<HttpResponseMessage>(client.GetAsync("products/KT-100", ct))).AsTask());
var total = clock.Elapsed;

var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("#").AddColumn(new TableColumn("At").RightAligned()).AddColumn("Status").AddColumn("Retry-After")
    .AddColumn("OnRetry got").AddColumn(new TableColumn("Delay").RightAligned()).AddColumn("Disposed");

foreach (var sent in handler.Sent)
{
    var retryAfter = sent.Response.Headers.RetryAfter?.Delta is { } delta ? $"{delta.TotalSeconds:F0} s" : "-";
    var status = ((int)sent.Response.StatusCode).ToString();
    if (retries.TryGetValue(sent.Attempt, out var retry))
    {
        // "no → yes": alive while OnRetry ran, disposed by the pipeline before the next attempt.
        table.AddRow(
            sent.Attempt.ToString(), $"{sent.At.TotalMilliseconds:F0} ms", $"[yellow]{status}[/]", retryAfter,
            retry.Kind, $"{retry.Delay.TotalMilliseconds:F0} ms",
            $"{YesNo(retry.DisposedDuringOnRetry)} → {YesNo(sent.Response.IsDisposed)}");
    }
    else
    {
        table.AddRow(
            sent.Attempt.ToString(), $"{sent.At.TotalMilliseconds:F0} ms", $"[green]{status}[/]", retryAfter,
            "-", "-", YesNo(sent.Response.IsDisposed));
    }
}

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

AnsiConsole.Write(new BarChart().Label("[deepskyblue1]Delay the strategy chose (ms)[/]").Width(60)
    .AddItem("after 503 (exponential)", retries[1].Delay.TotalMilliseconds, Color.Grey)
    .AddItem("after 429 (Retry-After)", retries[2].Delay.TotalMilliseconds, Color.DeepSkyBlue1));
AnsiConsole.WriteLine();

using (final)
{
    var body = await final.Content.ReadAsStringAsync();
    AnsiConsole.MarkupLine($"  [green]{(int)final.StatusCode} {final.StatusCode}[/] after {handler.Sent.Count} attempts in {total.TotalSeconds:F1} s");
    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(body)}[/]");
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  Disposed reads 'while OnRetry ran → after the call'.[/]");
AnsiConsole.MarkupLine("[grey]  OnRetry got each rejected response in Result, with LastException null.[/]");
AnsiConsole.MarkupLine("[grey]  The pipeline never disposes the response it returns: that one is yours.[/]");

static string YesNo(bool value) => value ? "yes" : "no";

// What OnRetry saw for one failed attempt.
sealed record RetryObservation(string Kind, TimeSpan Delay, bool DisposedDuringOnRetry);

// One request as the server saw it, with the response it returned.
sealed record SentRequest(int Attempt, TimeSpan At, TrackedResponse Response);

// A response that remembers whether anyone disposed it, so the demo can show what the pipeline did.
sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        this.IsDisposed = true;
        base.Dispose(disposing);
    }
}

// A fake server: it returns the scripted responses in order, with no network. The last entry repeats.
sealed class ScriptedHandler(params (HttpStatusCode Status, TimeSpan? RetryAfter)[] script) : HttpMessageHandler
{
    private readonly Stopwatch clock = new();

    public List<SentRequest> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.clock.Start();
        var (status, retryAfter) = script[Math.Min(this.Sent.Count, script.Length - 1)];
        var response = new TrackedResponse(status)
        {
            RequestMessage = request,
            Content = new StringContent(status == HttpStatusCode.OK
                ? """{"sku":"KT-100","name":"Kettle","price":24.99}"""
                : $$"""{"error":"{{status}}"}"""),
        };

        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        this.Sent.Add(new(this.Sent.Count + 1, this.clock.Elapsed, response));
        return Task.FromResult<HttpResponseMessage>(response);
    }
}
