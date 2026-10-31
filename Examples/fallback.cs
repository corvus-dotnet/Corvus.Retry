#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Shows RetryTaskFactory<T>.Fallback giving a product page a cached price when the catalogue service is
// down. The fallback applies only to the outcome of the attempts: its own Policy can refuse an exception, and an
// exception from a callback such as OnRetry bypasses it.
//
//   dotnet run Examples/fallback.cs

using System.Net;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Fallback").Color(Color.SpringGreen3).Centered());
AnsiConsole.Write(new Rule("[springgreen3]a cached answer when the attempts run out[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A product page asks the catalogue service for a price. A [white]10-minute-old cached price[/] is better than an " +
        "error page, so after [white]3 attempts[/] the factory falls back to the cache. A bug in our own request, or in " +
        "our own callbacks, must still surface.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

List<string> journal = new();

// OnFallback runs before the factory disposes a rejected result, so it can still log the body.
Func<FallbackEventArgs<HttpResponseMessage>, ValueTask> logFailure = async args =>
{
    if (args.Exception is { } ex)
    {
        journal.Add($"OnFallback logged '{ex.Message}'");
    }
    else if (args.Result is TrackedResponse response)
    {
        bool disposed = response.IsDisposed;
        string body = await response.Content.ReadAsStringAsync(args.CancellationToken);
        journal.Add($"OnFallback read {body}, disposed: {YesNo(disposed)}");
    }
};

// The action runs after that disposal: it receives the rejected result, already disposed. Do not read it here.
Func<FallbackEventArgs<HttpResponseMessage>, ValueTask<HttpResponseMessage>> useCache = args =>
{
    if (args.Result is TrackedResponse response)
    {
        journal.Add($"Action got the 503, disposed: {YesNo(response.IsDisposed)}");
    }

    journal.Add("Action returned the cache");
    return ValueTask.FromResult(Catalogue.Cached());
};

RetryTaskFactory<HttpResponseMessage> factory = new(
    RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(100)),

    // Retry transport failures and 5xx responses. An ArgumentException is a bug in our request: retrying cannot help.
    RetryPolicies.For<HttpRequestException>())
{
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,

    // The action is the constructor argument of Fallback<T>. The property and its type share the name Fallback, so a
    // target-typed new keeps the line short.
    Fallback = new(useCache)
    {
        // Only an outage earns the cached price. Any other exception propagates to the caller.
        Policy = RetryPolicies.For<HttpRequestException>(),
        AppliesToResult = response => response.StatusCode == HttpStatusCode.ServiceUnavailable,
        OnFallback = logFailure,
    },
};

// A factory is immutable, so the copy constructor makes the variant for scenario 4 without changing the original. Its
// fallback accepts every exception, to show that the OnRetry exception bypasses the fallback all the same.
RetryTaskFactory<HttpResponseMessage> auditedFactory = new(factory)
{
    OnRetry = _ => throw new InvalidOperationException("The audit log is full."),
    Fallback = new(useCache)
    {
        Policy = AnyExceptionPolicy.Default,
        AppliesToResult = response => response.StatusCode == HttpStatusCode.ServiceUnavailable,
        OnFallback = logFailure,
    },
};

Table table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("Scenario").AddColumn("What happened").AddColumn("Returned");

await AnsiConsole.Status().StartAsync("Asking the catalogue service…", async _ =>
{
    await Run("1. Outage", factory, Behaviour.Throw, "");
    await Run("2. 503 result", factory, Behaviour.Unavailable, "");
    await Run("3. Our bug", factory, Behaviour.BadRequest, "Fallback Policy refused it");
    await Run("4. OnRetry throws", auditedFactory, Behaviour.Throw, "Not an outcome of the attempts");
});

AnsiConsole.Write(table);
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]  The fallback is the outermost part of a call: it sees the outcome of the[/]");
AnsiConsole.MarkupLine("[grey]  attempts, and nothing else. Scenarios 3 and 4 reach the caller as exceptions.[/]");

async Task Run(string scenario, RetryTaskFactory<HttpResponseMessage> target, Behaviour behaviour, string whyNoFallback)
{
    journal.Clear();
    Catalogue service = new(behaviour);
    string value;
    try
    {
        using HttpResponseMessage response = await target.Run(ct => service.GetPriceAsync(ct));
        value = $"[green]{Markup.Escape(await response.Content.ReadAsStringAsync())}[/]";
    }
    catch (Exception ex)
    {
        journal.Add($"{ex.GetType().Name} propagated");
        value = $"[red]{ex.GetType().Name}[/]";
    }

    journal.Insert(0, service.Describe());
    if (!journal.Any(line => line.StartsWith("OnFallback", StringComparison.Ordinal)))
    {
        journal.Add($"No fallback: {whyNoFallback}");
    }

    table.AddRow(new Markup($"[springgreen3]{scenario}[/]"), new Markup(Markup.Escape(string.Join("\n", journal))), new Markup(value));
    table.AddEmptyRow();
}

static string YesNo(bool value) => value ? "yes" : "no";

enum Behaviour { Throw, Unavailable, BadRequest }

// A response that remembers whether anyone disposed it, so the demo can show when the factory does.
sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        this.IsDisposed = true;
        base.Dispose(disposing);
    }
}

// A simulated catalogue service that misbehaves in one scripted way, with no network.
sealed class Catalogue(Behaviour behaviour)
{
    public int Attempts { get; private set; }

    public static HttpResponseMessage Cached() =>
        new(HttpStatusCode.OK) { Content = new StringContent("£24.99 (cache)") };

    public string Describe() => behaviour switch
    {
        Behaviour.Throw => $"{this.Counted()} threw HttpRequestException",
        Behaviour.Unavailable => $"{this.Counted()} returned 503",
        _ => $"{this.Counted()} threw ArgumentException",
    };

    private string Counted() => this.Attempts == 1 ? "1 attempt" : $"{this.Attempts} attempts";

    public async Task<HttpResponseMessage> GetPriceAsync(CancellationToken cancellationToken)
    {
        this.Attempts++;
        await Task.Delay(50, cancellationToken);
        return behaviour switch
        {
            Behaviour.Throw => throw new HttpRequestException("Connection refused."),
            Behaviour.Unavailable => new TrackedResponse(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("\"down for maintenance\"") },
            _ => throw new ArgumentException("The SKU 'KT 100' contains a space.", "sku"),
        };
    }
}
