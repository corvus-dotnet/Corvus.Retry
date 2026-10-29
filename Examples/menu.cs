#:package Spectre.Console@0.57.2

// A launcher for the Corvus.Retry demos. In a terminal it presents a Spectre.Console menu and runs the
// chosen demo as its own file-based app (`dotnet run Examples/<name>.cs`). From a script it runs every
// demo, or the one you name, and reports how each ended.
//
//   dotnet run Examples/menu.cs                       (interactive menu)
//   dotnet run Examples/menu.cs -- --all              (run every demo, in order)
//   dotnet run Examples/menu.cs -- circuit-breaker    (run one demo)

using System.Diagnostics;

using Spectre.Console;

var demos = new (string Name, string File, string Blurb)[]
{
    ("Retry basics", "retry-basics.cs", "Retriable, the onRetry callback, cancellation and giving up"),
    ("Strategies", "strategies.cs", "Every delay sequence side by side, tries versus retries, jitter"),
    ("Policies", "policies.cs", "Which exceptions are worth another try: policies and combinators"),
    ("Thundering herd", "thundering-herd.cs", "Fifty clients retry at once: lockstep spikes versus jitter"),
    ("Timeouts", "timeouts-and-cancellation.cs", "Attempt timeout, total timeout and a caller's cancellation"),
    ("Result retries", "result-retries.cs", "Retry 503 and 429 responses, obey Retry-After, dispose the rest"),
    ("Fallback", "fallback.cs", "A cached answer when the service is down; your own bugs still surface"),
    ("Circuit breaker", "circuit-breaker.cs", "One breaker shared by two pipelines, half-open, isolate and close"),
    ("Hedging", "hedging.cs", "Race a second attempt against a slow one: p90 from 1.5 s to 300 ms"),
    ("Composed pipelines", "composed-pipelines.cs", "A retry pipeline around a hedging pipeline, traced attempt by attempt"),
    ("RetryTask", "retry-task.cs", "Eight flaky operations retried on the thread pool with live progress"),
    ("Queue consumer", "message-queue-consumer.cs", "ReliableTaskRunner restarts a consumer loop, stops on poison"),
    ("HTTP client", "http-client-resilience.cs", "One composed pipeline around HttpClient: 503, hang, 429, 500, 200"),
    ("Database startup", "database-startup.cs", "Wait for a dependency that is starting; fail fast on a wrong password"),
    ("Testing with fake time", "testing-with-fake-time.cs", "Minutes of back-off tested in milliseconds with a FakeTimeProvider"),
};

// Run from the repository root (`dotnet run Examples/menu.cs`) or from within Examples/.
string dir = File.Exists("retry-basics.cs") ? "." : "Examples";

AnsiConsole.Write(new FigletText("Retry").Color(Color.Aqua).Centered());
AnsiConsole.Write(new Rule("[aqua]Corvus.Retry demos[/]"));
AnsiConsole.WriteLine();

// From a script: run every demo, or the one named, and end with a summary. A Spectre menu needs an
// interactive terminal, so without one the launcher lists the demos instead of prompting.
if (args.Length > 0 || !AnsiConsole.Profile.Capabilities.Interactive || Console.IsInputRedirected)
{
    var selected = args.Contains("--all") || args.Length == 0
        ? demos
        : demos.Where(d => args.Any(a => string.Equals(a, d.Name, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a, d.File, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a + ".cs", d.File, StringComparison.OrdinalIgnoreCase))).ToArray();

    if (selected.Length == 0)
    {
        AnsiConsole.MarkupLine("[red]No demo matches the arguments.[/] Use [white]--all[/], or one of:");
        foreach (var d in demos)
        {
            AnsiConsole.MarkupLineInterpolated($"  [white]{Path.GetFileNameWithoutExtension(d.File)}[/]  [grey]{d.Blurb}[/]");
        }

        return 2;
    }

    if (args.Length == 0)
    {
        AnsiConsole.MarkupLine("[grey]No interactive terminal: running every demo. Pass a name to run one.[/]");
        AnsiConsole.WriteLine();
    }

    var summary = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
        .AddColumn("[white]Demo[/]")
        .AddColumn(new TableColumn("[white]Exit code[/]").RightAligned())
        .AddColumn(new TableColumn("[white]Time[/]").RightAligned());
    int failures = 0;

    foreach (var d in selected)
    {
        AnsiConsole.Write(new Rule($"[green]{Markup.Escape(d.Name)}[/]"));
        var watch = Stopwatch.StartNew();
        int exitCode = Run(dir, d.File);
        watch.Stop();
        failures += exitCode == 0 ? 0 : 1;
        summary.AddRow(
            Markup.Escape(d.Name),
            exitCode == 0 ? "[green]0[/]" : $"[red]{exitCode}[/]",
            $"{watch.Elapsed.TotalSeconds:F1} s");
        AnsiConsole.WriteLine();
    }

    AnsiConsole.Write(new Rule("[aqua]summary[/]"));
    AnsiConsole.Write(summary);
    AnsiConsole.MarkupLine(failures == 0
        ? $"[green]  All {selected.Length} demos exited 0.[/]"
        : $"[red]  {failures} of {selected.Length} demos failed.[/]");
    return failures == 0 ? 0 : 1;
}

const string quit = "Quit";

while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Choose a [green]demo[/] to run:")
            .PageSize(18)
            .UseConverter(name => name == quit
                ? "[grey]Quit[/]"
                : $"{name}  [grey]- {Markup.Escape(demos.First(d => d.Name == name).Blurb)}[/]")
            .AddChoices([.. demos.Select(d => d.Name), quit]));

    if (choice == quit)
    {
        break;
    }

    var demo = demos.First(d => d.Name == choice);

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule($"[green]{Markup.Escape(demo.Name)}[/]"));
    Run(dir, demo.File);

    AnsiConsole.WriteLine();
    if (!AnsiConsole.Confirm("Run [green]another[/] demo?"))
    {
        break;
    }

    AnsiConsole.WriteLine();
}

AnsiConsole.MarkupLine("[grey]Bye![/]");
return 0;

// Runs one demo as its own process and returns its exit code. The demo inherits this console, so its
// output appears in place, styled or plain as the console allows.
static int Run(string dir, string file)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    start.ArgumentList.Add("run");
    start.ArgumentList.Add(Path.Combine(dir, file));

    try
    {
        using var process = Process.Start(start);
        process!.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception ex)
    {
        // AnsiConsole.WriteException needs dynamic code, which the AOT analyser of a file-based app flags (IL3050).
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.GetType().Name}: {ex.Message}[/]");
        return 1;
    }
}
