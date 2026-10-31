#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:package Spectre.Console@0.57.2

// A factory can hedge or retry, not both: hedging is HedgedTaskFactory, retrying is RetryTaskFactory. To get both, nest
// them: an outer RetryTaskFactory that retries, falls back and limits each try, whose operation calls an inner
// HedgedTaskFactory that hedges each request behind a circuit breaker. The demo traces one call that needs the outer
// retry and two calls that the breaker and the fallback answer.
//
//   dotnet run Examples/composed-factories.cs

using System.Net;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("Composed").Color(Color.MediumPurple).Centered());
AnsiConsole.Write(new Rule("[mediumpurple]retry around hedging, a breaker inside, a fallback outside[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "Hedging and retrying are two different factories, so [white]one factory cannot do both[/]. " +
        "Two factories can: the [white]outer[/] one retries a whole hedged call, the [white]inner[/] one races " +
        "[white]two attempts[/] per call. A breaker guards the service and a fallback answers when it is down.")
    .Header("[white]The problem[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

ScriptedService service = new();
Trace trace = new();

// The circuit breaker sits on the INNER factory, so it counts every request that reaches the service, hedged ones
// included. Do not also give the same instance to the outer factory: when the circuit is half-open, the outer attempt
// would take the one test call, and the inner attempt inside it would then be rejected, so the circuit could never
// close again. minimumCalls is low so that the demo opens it in a few calls.
CircuitBreaker breaker = new(
    failureRate: 0.5,
    minimumCalls: 6,
    openDuration: TimeSpan.FromSeconds(30),
    onStateChanged: change =>
    {
        trace.Add($"[red]circuit {change.From} → {change.To}[/]");
        trace.BreakerOpened += change.To == CircuitState.Open ? 1 : 0;
        return ValueTask.CompletedTask;
    });

// INNER: hedging. After 150 ms with no answer, or at once after a failure, start a second attempt. Its policy is
// TransientHttp, so a CircuitRejectedException is not a "failure to hedge": it leaves the inner factory immediately.
// Each hedged attempt has its own 1 s AttemptTimeout.
HedgedTaskFactory<string> inner = new(extraAttempts: 1, stagger: TimeSpan.FromMilliseconds(150))
{
    Policy = RetryPolicies.TransientHttp,
    OnHedgedAttempt = args =>
    {
        trace.Hedges++;
        trace.Add($"[grey]OnHedgedAttempt: start hedged attempt {args.AttemptNumber} after {args.LastException?.GetType().Name ?? "the stagger"}[/]");
        return ValueTask.CompletedTask;
    },
    CircuitBreaker = breaker,
    AttemptTimeout = TimeSpan.FromSeconds(1),
};

// OUTER: retry, timeout, fallback. Exponential gives 200 ms then 400 ms between whole hedged calls. TransientHttp
// retries the 503 that the inner factory throws when both hedged attempts fail, but not a CircuitRejectedException:
// retrying into an open circuit is pointless. The 3 s AttemptTimeout bounds one whole hedged call. The fallback
// answers with a cached value for anything that gets out of the retries.
RetryTaskFactory<string> outer = new(
    RetryStrategies.Exponential(retries: 2, baseDelay: TimeSpan.FromMilliseconds(200)),
    RetryPolicies.TransientHttp)
{
    AttemptTimeout = TimeSpan.FromSeconds(3),
    OnRetry = args =>
    {
        trace.Retries++;
        trace.Add($"[yellow]outer OnRetry: attempt {args.AttemptNumber} failed, attempt {args.NextAttemptNumber} in {args.DelayBeforeRetry.TotalMilliseconds:F0} ms[/]");
        return ValueTask.CompletedTask;
    },
    Fallback = new(_ => ValueTask.FromResult("cached price £41.50"))
    {
        OnFallback = args =>
        {
            trace.Fallbacks++;
            trace.AddToCall($"[mediumpurple]fallback after {args.Exception?.GetType().Name}[/]");
            return ValueTask.CompletedTask;
        },
    },
};

Tree root = new("[mediumpurple]outer.Run(ct => inner.Run(service.GetPriceAsync, ct))[/]");
foreach ((string? title, ServiceMode mode) in new[]
{
    ("Call 1: the service has a bad moment", ServiceMode.FailTwiceThenRecover),
    ("Call 2: the service goes down", ServiceMode.Down),
    ("Call 3: the service is still down", ServiceMode.Down),
})
{
    service.Mode = mode;
    TreeNode callNode = root.AddNode($"[white]{title}[/]");
    trace.Start(callNode);
    int outerAttempt = 0;

    // The outer operation is one whole hedged call. It passes its own token on, so the outer AttemptTimeout and the
    // caller's cancellation reach every hedged attempt.
    string answer = await outer.Run(async ct =>
    {
        trace.BeginOuterAttempt(++outerAttempt);
        try
        {
            string value = await inner.Run(c => service.GetPriceAsync(trace, c), ct);
            trace.Add($"[green]inner factory returned \"{value}\"[/]");
            return value;
        }
        catch (Exception ex)
        {
            if (ex is CircuitRejectedException)
            {
                trace.Add("[red]the open circuit rejected the attempt: no request reached the service[/]");
            }

            trace.Add($"[red]inner factory threw {ex.GetType().Name}[/]");
            throw;
        }
    });

    callNode.AddNode($"[white]→ caller receives \"{answer}\"[/]");
}

AnsiConsole.Write(root);
AnsiConsole.WriteLine();

Table layers = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("layer").AddColumn("part").AddColumn("what it did in these three calls");
layers.AddRow("outer", "Fallback", $"answered {trace.Fallbacks} calls with the cached price");
layers.AddRow("outer", "Retry: Exponential, TransientHttp", $"retried {trace.Retries} whole hedged calls; did not retry the open circuit");
layers.AddRow("outer", "AttemptTimeout 3 s", "bounded each hedged call (never needed here)");
layers.AddRow("inner", "Hedging: 150 ms, 1 extra attempt", $"started {trace.Hedges} hedged attempts");
layers.AddRow("inner", "CircuitBreaker", $"saw {service.Requests} requests, opened {trace.BreakerOpened} time, then rejected without a request");
layers.AddRow("inner", "AttemptTimeout 1 s", "bounded each hedged attempt (never needed here)");
AnsiConsole.Write(layers);
AnsiConsole.MarkupLine("[grey]  Read the tree from the outside in: outer attempts contain hedged attempts;[/]");
AnsiConsole.MarkupLine("[grey]  the breaker and the fallback end the story.[/]");

public enum ServiceMode
{
    FailTwiceThenRecover,
    Down,
}

// A price service. In FailTwiceThenRecover mode the first two requests answer 503 and the rest succeed; in Down mode
// every request answers 503.
public sealed class ScriptedService
{
    private int requestsInMode;

    public ServiceMode Mode
    {
        get;
        set
        {
            field = value;
            this.requestsInMode = 0;
        }
    }

    public int Requests { get; private set; }

    public async Task<string> GetPriceAsync(Trace trace, CancellationToken cancellationToken)
    {
        int number = ++this.Requests;
        bool fails = this.Mode == ServiceMode.Down || ++this.requestsInMode <= 2;
        await Task.Delay(80, cancellationToken);
        if (fails)
        {
            trace.Add($"request {number}: [red]503 Service Unavailable[/]");
            throw new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        }

        trace.Add($"request {number}: [green]200 OK[/]");
        return "live price £42.00";
    }
}

// Collects what happened into the tree: each outer attempt is a node, and everything inside it hangs below.
public sealed class Trace
{
    private readonly Lock gate = new();
    private TreeNode? call;
    private TreeNode? current;

    public int Retries { get; set; }

    public int Hedges { get; set; }

    public int Fallbacks { get; set; }

    public int BreakerOpened { get; set; }

    public void Start(TreeNode callNode) => (this.call, this.current) = (callNode, callNode);

    public void BeginOuterAttempt(int number) => this.current = this.call!.AddNode($"[mediumpurple]outer attempt {number}[/]");

    public void Add(string markup)
    {
        lock (this.gate)
        {
            this.current!.AddNode(markup);
        }
    }

    public void AddToCall(string markup) => this.call!.AddNode(markup);
}
