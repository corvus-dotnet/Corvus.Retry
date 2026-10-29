#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// Shows the two time limits of a ReliabilityPipeline and how they differ from the caller's own cancellation.
// AttemptTimeout cuts one slow attempt short so that a retry can succeed; TotalTimeout caps the whole call, delays
// included. Both cancel the token that the operation receives. A cancellation by the caller is never retried.
//
//   dotnet run Examples/timeouts-and-cancellation.cs

using System.Diagnostics;
using System.Net;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Timeouts").Color(Color.Orange1).Centered());
AnsiConsole.Write(new Rule("[orange1]AttemptTimeout, TotalTimeout and the caller's cancellation[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "A pricing service usually answers in [white]100–150 ms[/], but now and then takes [white]900 ms[/]. We want to " +
        "abandon a slow attempt at [white]400 ms[/] and try again, cap a failing call at [white]600 ms[/] in total, " +
        "and stop at once when the user goes away.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

// ── 1. An attempt timeout turns the slow attempt into a retry ────────────────────────────────────────────────────
{
    var service = new PricingService(latencies: [900, 150]);
    var asked = new List<string>();
    var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
    {
        // 400 ms is well above the usual latency, so only the slow outlier is cut short.
        AttemptTimeout = TimeSpan.FromMilliseconds(400),
        Strategy = RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(100)),

        // A timed-out attempt fails with RetryTimeoutException. The policy must accept it, or there is no retry.
        Policy = Recording(asked, RetryPolicies.Handle<RetryTimeoutException>().Or<HttpRequestException>()),
    });

    var verdict = await RunAsync("Calling the pricing service", async () =>
    {
        var price = await pipeline.ExecuteAsync(ct => service.GetPriceAsync(ct));
        return $"[green]Returned £{price:0.00}[/] after {service.Attempts.Count} attempts.";
    });

    Render("Run 1 · AttemptTimeout = 400 ms", service, verdict, $"Policy was asked about: {string.Join(", ", asked)}");
}

// ── 2. A total timeout stops a delay that would overrun it ───────────────────────────────────────────────────────
{
    var service = new PricingService(latencies: [100, 100, 100], fail: true);
    var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
    {
        // The total timeout covers the attempts and the delays. The exponential delays (200 ms, then 400 ms) would end
        // after 800 ms, so the limit expires during the second delay and no third attempt starts.
        TotalTimeout = TimeSpan.FromMilliseconds(600),
        Strategy = RetryStrategies.Exponential(retries: 3, baseDelay: TimeSpan.FromMilliseconds(200)),
    });

    var clock = Stopwatch.StartNew();
    var verdict = await RunAsync("Calling the pricing service", async () =>
    {
        try
        {
            await pipeline.ExecuteAsync(ct => service.GetPriceAsync(ct));
            return "[red]Unexpected success[/]";
        }
        catch (RetryTimeoutException ex)
        {
            return $"[yellow]RetryTimeoutException[/] after {clock.ElapsedMilliseconds} ms\n" +
                   $"    Timeout        = {ex.Timeout.TotalMilliseconds} ms\n" +
                   $"    InnerException = {Markup.Escape(ex.InnerException?.GetType().Name ?? "null")}: " +
                   Markup.Escape(ex.InnerException?.Message ?? string.Empty);
        }
    });

    Render("Run 2 · TotalTimeout = 600 ms", service, verdict, "The 400 ms delay after attempt 2 would end at 800 ms: the limit cut it short.");
}

// ── 3. The caller cancels during an attempt ──────────────────────────────────────────────────────────────────────
{
    var service = new PricingService(latencies: [900]);
    var asked = new List<string>();
    var pipeline = new ReliabilityPipeline(new ReliabilityPipelineOptions
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(400),
        Strategy = RetryStrategies.Constant(retries: 2, delay: TimeSpan.FromMilliseconds(100)),

        // This policy accepts every exception, yet it is never asked: the pipeline does not retry the caller's cancellation.
        Policy = Recording(asked, AnyExceptionPolicy.Default),
    });

    // The user navigates away 250 ms into the first attempt, before the attempt timeout.
    using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    var verdict = await RunAsync("Calling the pricing service", async () =>
    {
        try
        {
            await pipeline.ExecuteAsync(ct => service.GetPriceAsync(ct), caller.Token);
            return "[red]Unexpected success[/]";
        }
        catch (OperationCanceledException ex)
        {
            return $"[yellow]{ex.GetType().Name}[/]: carries the caller's token = {ex.CancellationToken == caller.Token}.";
        }
    });

    Render("Run 3 · The caller cancels at 250 ms", service, verdict, asked.Count == 0 ? "The policy was never asked." : $"Policy was asked about: {string.Join(", ", asked)}");
}

AnsiConsole.MarkupLine("[grey]  Each 'token cancelled' row is the service seeing its token cancelled.[/]");
AnsiConsole.MarkupLine("[grey]  The timeouts are cooperative: they work only if the operation passes the token on.[/]");

// Runs one call behind a spinner, so the waits are visible rather than dead time.
static Task<string> RunAsync(string label, Func<Task<string>> call) =>
    AnsiConsole.Status().Spinner(Spinner.Known.Clock).StartAsync($"{label}…", _ => call());

// Wraps a policy so that the demo can show which exceptions the pipeline asked it about.
static IRetryPolicy Recording(List<string> asked, IRetryPolicy inner) =>
    RetryPolicies.FromPredicate(ex =>
    {
        asked.Add(ex.GetType().Name);
        return inner.CanRetry(ex);
    });

// Draws the attempts as a timeline. One character is 40 ms, so that the table fits in 80 columns.
static void Render(string title, PricingService service, string verdict, string note)
{
    var (idle, busy) = AnsiConsole.Profile.Capabilities.Unicode ? ('·', '█') : ('.', '#');
    var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[orange1]{Markup.Escape(title)}[/]")
        .AddColumn("Attempt").AddColumn(new TableColumn("Started").RightAligned()).AddColumn(new TableColumn("Ended").RightAligned())
        .AddColumn("Outcome").AddColumn("Timeline (40 ms/char)");

    foreach (var attempt in service.Attempts)
    {
        var start = (int)(attempt.Started.TotalMilliseconds / 40);
        var length = Math.Max(1, (int)((attempt.Ended - attempt.Started).TotalMilliseconds / 40));
        table.AddRow(
            attempt.Number.ToString(),
            $"{attempt.Started.TotalMilliseconds:F0} ms",
            $"{attempt.Ended.TotalMilliseconds:F0} ms",
            attempt.Outcome,
            $"[grey]{new string(idle, start)}[/][orange1]{new string(busy, length)}[/]");
    }

    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"  {verdict}");
    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(note)}[/]");
    AnsiConsole.WriteLine();
}

// One attempt as the service saw it.
sealed record AttemptRecord(int Number, TimeSpan Started, TimeSpan Ended, string Outcome);

// A simulated pricing service. Each call takes the next latency from the script and honours the cancellation token,
// as a real HttpClient call would. With fail set, each call throws after its latency, like a 503 response.
sealed class PricingService(int[] latencies, bool fail = false)
{
    // The clock starts at the first call, so the timeline begins at 0 ms.
    private Stopwatch? clock;

    public List<AttemptRecord> Attempts { get; } = [];

    public async ValueTask<decimal> GetPriceAsync(CancellationToken cancellationToken)
    {
        this.clock ??= Stopwatch.StartNew();
        var number = this.Attempts.Count + 1;
        var started = this.clock.Elapsed;
        var latency = latencies[Math.Min(number - 1, latencies.Length - 1)];
        try
        {
            await Task.Delay(latency, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            this.Attempts.Add(new(number, started, this.clock.Elapsed, "[yellow]token cancelled[/]"));
            throw;
        }

        if (fail)
        {
            this.Attempts.Add(new(number, started, this.clock.Elapsed, "[red]503[/]"));
            throw new HttpRequestException($"Attempt {number}: 503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        }

        this.Attempts.Add(new(number, started, this.clock.Elapsed, "[green]200 OK[/]"));
        return 24.99m;
    }
}
