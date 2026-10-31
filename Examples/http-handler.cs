#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:project ../Solutions/Corvus.Retry.Extensions.Http/Corvus.Retry.Extensions.Http.csproj
#:package Microsoft.Extensions.DependencyInjection@10.0.0
#:package Spectre.Console@0.57.2

// RetryHandler puts a RetryTaskFactory<HttpResponseMessage> inside HttpClient, as a DelegatingHandler, so the call site
// is a plain HttpClient call with no wrapper. A GET is retried, a POST gets one attempt unless you opt it in, and
// AddRetryHandler (in Corvus.Retry.Extensions.Http) adds the same handler to a client from IHttpClientFactory. The
// service is a scripted HttpMessageHandler, so the demo needs no network.
//
//   dotnet run Examples/http-handler.cs

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;

using Spectre.Console;

using Corvus.Retry;
using Corvus.Retry.Http;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

AnsiConsole.Write(new FigletText("HTTP Handler").Color(Color.DeepSkyBlue1).Centered());
AnsiConsole.Write(new Rule("[deepskyblue1]retries inside HttpClient, not around each call[/]"));
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "Wrapping every call in [white]factory.Run(ct => client.GetAsync(uri, ct))[/] works, but it is easy to forget one. " +
        "[white]RetryHandler[/] is a [white]DelegatingHandler[/]: give it a factory once, and every request that the client " +
        "sends gets the retries, timeouts, circuit breaker and fallback of that factory.")
    .Header("[white]The idea[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var log = new List<string>();
var clock = new Stopwatch();

// Make the factory once and share it: it is immutable, and every handler that uses it shares its circuit breaker.
var retries = new RetryTaskFactory<HttpResponseMessage>(
    // The server's Retry-After header wins when it sends one; otherwise exponential backoff from 200 ms.
    RetryStrategies.RetryAfter(RetryStrategies.Exponential(retries: 4, baseDelay: TimeSpan.FromMilliseconds(200))),
    RetryPolicies.TransientHttp)
{
    // HttpClient returns 408, 429 and 5xx as responses, so the factory must examine results as well as exceptions.
    CanRetryResult = RetryPolicies.IsTransientHttpResponse,
    AttemptTimeout = TimeSpan.FromSeconds(2),

    // Shorter than HttpClient.Timeout (100 s by default), so that the factory stops first.
    TotalTimeout = TimeSpan.FromSeconds(10),
    OnRetry = args =>
    {
        var response = (HttpResponseMessage?)args.Result;
        string outcome = response is null ? Markup.Escape(args.LastException?.GetType().Name ?? "?") : $"{(int)response.StatusCode}";
        string why = response?.Headers.RetryAfter is not null ? "[white]Retry-After[/]" : "strategy";
        log.Add($"  [red]attempt {args.AttemptNumber}: {outcome}[/] at {clock.ElapsedMilliseconds} ms, retry in {args.DelayBeforeRetry.TotalMilliseconds:F0} ms ({why})");
        return ValueTask.CompletedTask;
    },
};

// 1. A GET: the call site is plain HttpClient code.
var catalogue = new ScriptedHandler(
    Step.Status(HttpStatusCode.ServiceUnavailable),
    Step.TooManyRequests(retryAfter: TimeSpan.FromSeconds(1)),
    Step.Json("""{ "id": 42, "name": "Raven feeder", "stock": 7 }"""));
using (var client = new HttpClient(new RetryHandler(retries, catalogue)) { BaseAddress = new Uri("https://catalogue.example/") })
{
    string body = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
    Show("1. GET /products/42", catalogue, body, Color.Green);
}

// 2. A POST is not idempotent, so by default it gets one attempt (with the same timeouts, breaker and fallback).
var orders = new ScriptedHandler(Step.Status(HttpStatusCode.ServiceUnavailable), Step.Status(HttpStatusCode.Created));
using (var client = new HttpClient(new RetryHandler(retries, orders)) { BaseAddress = new Uri("https://orders.example/") })
{
    using HttpResponseMessage response = await RunAsync("POST /orders", () => client.PostAsync("orders", new StringContent("""{ "sku": 42 }""")));
    Show("2. POST /orders, default CanRetryRequest", orders, $"{(int)response.StatusCode} {response.StatusCode}", Color.Orange1);
}

// 3. The service ignores a repeated order with the same idempotency key, so this client opts POST in.
var idempotentOrders = new ScriptedHandler(Step.Status(HttpStatusCode.ServiceUnavailable), Step.Status(HttpStatusCode.Created));
var optedIn = new RetryHandler(retries, idempotentOrders) { CanRetryRequest = RetryHandler.CanReplayContent };
using (var client = new HttpClient(optedIn) { BaseAddress = new Uri("https://orders.example/") })
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "orders") { Content = new StringContent("""{ "sku": 42 }""") };
    request.Headers.Add("Idempotency-Key", "order-7f3a");
    using HttpResponseMessage response = await RunAsync("POST /orders", () => client.SendAsync(request));
    Show("3. POST /orders, CanRetryRequest = RetryHandler.CanReplayContent", idempotentOrders, $"{(int)response.StatusCode} {response.StatusCode}", Color.Green);
}

// 4. IHttpClientFactory: AddRetryHandler adds a new RetryHandler to each handler chain, and every one uses this factory.
var factoryService = new ScriptedHandler(Step.Status(HttpStatusCode.BadGateway), Step.Json("""{ "id": 42, "stock": 7 }"""));
var services = new ServiceCollection();
services.AddHttpClient("catalogue", c => c.BaseAddress = new Uri("https://catalogue.example/"))
    .ConfigurePrimaryHttpMessageHandler(() => factoryService)
    .AddRetryHandler(retries);
await using (ServiceProvider provider = services.BuildServiceProvider())
{
    HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("catalogue");
    string body = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
    Show("4. services.AddHttpClient(\"catalogue\").AddRetryHandler(retries)", factoryService, body, Color.Green);
}

AnsiConsole.Write(new Panel(
        "[white]Wrap the call instead[/] ([white]factory.Run(ct => client.GetStringAsync(uri, ct))[/]) when:\n" +
        "• A failure while the body downloads must be retried. The handler returns when the headers arrive, and " +
        "HttpClient reads a buffered body after that, so a broken body is not retried.\n" +
        "• The attempt timeout must cover the body too. AttemptTimeout ends when the handler returns.\n" +
        "• You want to hedge. Attempts at the same time cannot share one HttpRequestMessage.\n" +
        "• You call the synchronous HttpClient.Send. The handler throws NotSupportedException.\n\n" +
        "[white]HttpClient.Timeout[/] sits outside the handler: it limits all the attempts and delays, and throws a " +
        "TaskCanceledException. Keep the factory's TotalTimeout shorter.")
    .Header("[white]When to wrap instead[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));

async Task<T> RunAsync<T>(string status, Func<Task<T>> call)
{
    log.Clear();
    clock.Restart();
    return await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(status, _ => call());
}

void Show(string title, ScriptedHandler handler, string result, Color colour)
{
    AnsiConsole.Write(new Rule($"[deepskyblue1]{Markup.Escape(title)}[/]").LeftJustified());
    foreach (var line in log)
    {
        AnsiConsole.MarkupLine(line);
    }

    AnsiConsole.MarkupLine($"  The service saw [white]{handler.Calls.Count}[/] request(s): {string.Join(", ", handler.Calls)}");
    AnsiConsole.Write(new Panel(Markup.Escape(result)).Header("[white]The caller got[/]").Border(BoxBorder.Rounded).BorderColor(colour).Expand());
    AnsiConsole.WriteLine();
}

// One scripted answer: a description for the log, and the response that the handler gives.
internal sealed record Step(string Description, Func<HttpResponseMessage> Respond)
{
    public static Step Status(HttpStatusCode code) => new($"{(int)code}", () => new HttpResponseMessage(code));

    public static Step TooManyRequests(TimeSpan retryAfter) =>
        new($"429 (Retry-After: {retryAfter.TotalSeconds:F0})", () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
        });

    public static Step Json(string json) =>
        new("200", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
}

// An HttpMessageHandler that plays a script, one step for each request. The last step repeats.
internal sealed class ScriptedHandler(params Step[] steps) : HttpMessageHandler
{
    public List<string> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var step = steps[Math.Min(this.Calls.Count, steps.Length - 1)];
        this.Calls.Add($"{request.Method} {step.Description}");
        return Task.FromResult(step.Respond());
    }
}
