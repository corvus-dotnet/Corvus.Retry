#:project ../Solutions/Corvus.Retry/Corvus.Retry.csproj
#:project ../Solutions/Corvus.Retry.Extensions.Http/Corvus.Retry.Extensions.Http.csproj
#:package Microsoft.Extensions.DependencyInjection@10.0.0
#:package Spectre.Console@0.57.2

// RetryHandler puts a RetryTaskFactory<HttpResponseMessage> inside HttpClient, as a DelegatingHandler, so the call site
// is a plain HttpClient call with no wrapper. A GET is retried, a POST gets one attempt unless you opt it in, and
// AddRetryHandler (in Corvus.Retry.Extensions.Http) adds the same handler to a client from IHttpClientFactory. The
// demo then shows the limits of a handler: a body that breaks after the headers is not retried; a circuit breaker and a
// fallback inside the client; and which exception the caller sees for each kind of timeout and cancellation. The
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

List<string> log = new();
Stopwatch clock = new();

// Make the factory once and share it: it is immutable, and every handler that uses it shares its settings (and its
// circuit breaker, when it has one: see section 6).
RetryTaskFactory<HttpResponseMessage> retries = new(
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
        HttpResponseMessage? response = (HttpResponseMessage?)args.Result;
        string outcome = response is null ? Markup.Escape(args.LastException?.GetType().Name ?? "?") : $"{(int)response.StatusCode}";
        string why = response?.Headers.RetryAfter is not null ? "[white]Retry-After[/]" : "strategy";
        log.Add($"  [red]attempt {args.AttemptNumber}: {outcome}[/] at {clock.ElapsedMilliseconds} ms, retry in {args.DelayBeforeRetry.TotalMilliseconds:F0} ms ({why})");
        return ValueTask.CompletedTask;
    },
};

// 1. A GET: the call site is plain HttpClient code.
ScriptedHandler catalogue = new(
    Step.Status(HttpStatusCode.ServiceUnavailable),
    Step.TooManyRequests(retryAfter: TimeSpan.FromSeconds(1)),
    Step.Json("""{ "id": 42, "name": "Raven feeder", "stock": 7 }"""));
using (HttpClient client = new(new RetryHandler(retries, catalogue)) { BaseAddress = new("https://catalogue.example/") })
{
    string body = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
    Show("1. GET /products/42", catalogue, body, Color.Green);
}

// 2. A POST is not idempotent, so by default it gets one attempt (with the same timeouts, breaker and fallback).
ScriptedHandler orders = new(Step.Status(HttpStatusCode.ServiceUnavailable), Step.Status(HttpStatusCode.Created));
using (HttpClient client = new(new RetryHandler(retries, orders)) { BaseAddress = new("https://orders.example/") })
{
    using HttpResponseMessage response = await RunAsync("POST /orders", () => client.PostAsync("orders", new StringContent("""{ "sku": 42 }""")));
    Show("2. POST /orders, default CanRetryRequest", orders, $"{(int)response.StatusCode} {response.StatusCode}", Color.Orange1);
}

// 3. The service ignores a repeated order with the same idempotency key, so this client opts POST in.
ScriptedHandler idempotentOrders = new(Step.Status(HttpStatusCode.ServiceUnavailable), Step.Status(HttpStatusCode.Created));
RetryHandler optedIn = new(retries, idempotentOrders) { CanRetryRequest = RetryHandler.CanReplayContent };
using (HttpClient client = new(optedIn) { BaseAddress = new("https://orders.example/") })
{
    using HttpRequestMessage request = new(HttpMethod.Post, "orders") { Content = new StringContent("""{ "sku": 42 }""") };
    request.Headers.Add("Idempotency-Key", "order-7f3a");
    using HttpResponseMessage response = await RunAsync("POST /orders", () => client.SendAsync(request));
    Show("3. POST /orders, CanRetryRequest = RetryHandler.CanReplayContent", idempotentOrders, $"{(int)response.StatusCode} {response.StatusCode}", Color.Green);
}

// 4. IHttpClientFactory: AddRetryHandler adds a new RetryHandler to each handler chain, and every one uses this factory.
// A named client gets the default CanRetryRequest. A typed client for the orders service passes RetryHandler.CanReplayContent,
// so its POSTs are retried too: the service ignores a repeated order with the same idempotency key.
ScriptedHandler factoryService = new(Step.Status(HttpStatusCode.BadGateway), Step.Json("""{ "id": 42, "stock": 7 }"""));
ScriptedHandler typedOrders = new(Step.Status(HttpStatusCode.ServiceUnavailable), Step.Status(HttpStatusCode.Created));
ServiceCollection services = new();
services.AddHttpClient("catalogue", c => c.BaseAddress = new("https://catalogue.example/"))
    .ConfigurePrimaryHttpMessageHandler(() => factoryService)
    .AddRetryHandler(retries);
services.AddHttpClient<OrdersClient>(c => c.BaseAddress = new("https://orders.example/"))
    .ConfigurePrimaryHttpMessageHandler(() => typedOrders)
    .AddRetryHandler(retries, RetryHandler.CanReplayContent);
await using (ServiceProvider provider = services.BuildServiceProvider())
{
    HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("catalogue");
    string body = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
    Show("4a. services.AddHttpClient(\"catalogue\").AddRetryHandler(retries)", factoryService, body, Color.Green);

    OrdersClient ordersClient = provider.GetRequiredService<OrdersClient>();
    HttpStatusCode status = await RunAsync("POST /orders", () => ordersClient.PlaceOrderAsync(sku: 42, idempotencyKey: "order-7f3a"));
    Show("4b. typed OrdersClient: AddRetryHandler(retries, CanReplayContent)", typedOrders, $"{(int)status} {status}", Color.Green);
}

// 5. The handler returns the response when the headers arrive. HttpClient reads the body after that, outside the
// handler, so a body that breaks is not retried: the caller gets the HttpRequestException that HttpClient makes from the
// IOException, and the service saw one request. To retry this, wrap the call: Examples/http-client-resilience.cs does.
ScriptedHandler broken = new(Step.BrokenBody(), Step.Json("""{ "id": 42, "name": "Raven feeder", "stock": 7 }"""));
using (HttpClient client = new(new RetryHandler(retries, broken)) { BaseAddress = new("https://catalogue.example/") })
{
    string outcome;
    try
    {
        outcome = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
    }
    catch (HttpRequestException ex)
    {
        outcome = $"{ex.GetType().Name}: {ex.Message} (inner: {ex.InnerException?.GetType().Name})";
    }

    Show("5. GET /products/42, the body breaks after the headers: not retried", broken, outcome, Color.Orange1);
}

// 6. A circuit breaker and a fallback inside the client. The copy constructor adds them to the shared factory. The
// small minimumCalls lets the demo open the circuit within one call; a real service uses the defaults.
CircuitBreaker breaker = new(
    failureRate: 0.5,
    minimumCalls: 2,
    openDuration: TimeSpan.FromSeconds(5),
    onStateChanged: change =>
    {
        log.Add($"  [orange1]circuit {change.From} → {change.To}[/] at {clock.ElapsedMilliseconds} ms");
        return ValueTask.CompletedTask;
    });
RetryTaskFactory<HttpResponseMessage> guarded = new(retries)
{
    CircuitBreaker = breaker,
    Fallback = new(_ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "id": 42, "name": "Raven feeder", "stock": null, "cached": "10 min ago" }"""),
    }))
    {
        // The cache answers an open circuit, and a response that is still transient when the retries run out. Any
        // other exception, such as a timeout, still reaches the caller: section 7 shows which.
        Policy = RetryPolicies.For<CircuitRejectedException>(),
        AppliesToResult = RetryPolicies.IsTransientHttpResponse,
        OnFallback = args =>
        {
            log.Add($"  [orange1]fallback[/] after {(args.Exception is { } ex ? ex.GetType().Name : $"a {(int?)args.Result?.StatusCode} response")} at {clock.ElapsedMilliseconds} ms");
            return ValueTask.CompletedTask;
        },
    },
};
ScriptedHandler down = new(Step.Status(HttpStatusCode.ServiceUnavailable));
using (HttpClient client = new(new RetryHandler(guarded, down)) { BaseAddress = new("https://catalogue.example/") })
{
    // Call 1: 503, 503, and the circuit opens; the third attempt is rejected, TransientHttp does not retry a
    // CircuitRejectedException, and the fallback answers. Call 2: rejected at once, no request, the fallback answers.
    for (int call = 1; call <= 2; call++)
    {
        string body = await RunAsync("GET /products/42", () => client.GetStringAsync("products/42"));
        Show($"6{(call == 1 ? "a" : "b")}. GET /products/42 while the service is down, call {call}: breaker and fallback", down, body, Color.Orange1);
    }
}

// 7. Which exception the caller sees. A factory with two attempts of 300 ms each, and no fallback.
RetryTaskFactory<HttpResponseMessage> timed = new(RetryStrategies.Constant(retries: 1, delay: TimeSpan.FromMilliseconds(100)), RetryPolicies.TransientHttp)
{
    AttemptTimeout = TimeSpan.FromMilliseconds(300),
};
Table exceptions = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("Cause").AddColumn("Exception").AddColumn("Detail").AddColumn(new TableColumn("Requests").RightAligned()).AddColumn(new TableColumn("Time").RightAligned());

// a. The factory's attempt timeout, on both attempts. The handler throws RetryTimeoutException, and HttpClient passes it on.
ScriptedHandler hanging = new(Step.Hang());
using (HttpClient client = new(new RetryHandler(timed, hanging)) { BaseAddress = new("https://catalogue.example/") })
{
    Stopwatch watch = Stopwatch.StartNew();
    (string type, string detail) = await CatchAsync(() => client.GetAsync("products/42"));
    exceptions.AddRow("AttemptTimeout 300 ms, twice", type, detail, $"{hanging.Calls.Count}", $"{watch.ElapsedMilliseconds} ms");
}

// b. HttpClient.Timeout sits outside the handler: it cancels the attempts and the delays together. HttpClient throws
// TaskCanceledException with an inner TimeoutException. The handler saw its caller cancel, so it did not retry, and a
// fallback would not have applied.
ScriptedHandler hangingToo = new(Step.Hang());
using (HttpClient client = new(new RetryHandler(timed, hangingToo)) { BaseAddress = new("https://catalogue.example/"), Timeout = TimeSpan.FromMilliseconds(500) })
{
    Stopwatch watch = Stopwatch.StartNew();
    (string type, string detail) = await CatchAsync(() => client.GetAsync("products/42"));
    exceptions.AddRow("HttpClient.Timeout 500 ms", type, detail, $"{hangingToo.Calls.Count}", $"{watch.ElapsedMilliseconds} ms");
}

// c. The caller cancels: TaskCanceledException that carries the caller's token. Never retried.
ScriptedHandler hangingStill = new(Step.Hang());
using (HttpClient client = new(new RetryHandler(timed, hangingStill)) { BaseAddress = new("https://catalogue.example/") })
{
    using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(150));
    Stopwatch watch = Stopwatch.StartNew();
    string type = "none", detail = "returned";
    try
    {
        await client.GetAsync("products/42", caller.Token);
    }
    catch (OperationCanceledException ex)
    {
        (type, detail) = (ex.GetType().Name, $"carries the caller's token: {ex.CancellationToken == caller.Token}");
    }

    exceptions.AddRow("The caller cancels at 150 ms", type, detail, $"{hangingStill.Calls.Count}", $"{watch.ElapsedMilliseconds} ms");
}

AnsiConsole.Write(new Rule("[deepskyblue1]7. Which exception the caller sees[/]").LeftJustified());
AnsiConsole.Write(exceptions);
AnsiConsole.WriteLine();

AnsiConsole.Write(new Panel(
        "[white]Wrap the call instead[/] ([white]factory.Run(ct => client.GetStringAsync(uri, ct))[/]) when:\n" +
        "• A failure while the body downloads must be retried. The handler returns when the headers arrive, and " +
        "HttpClient reads a buffered body after that, so a broken body is not retried (section 5).\n" +
        "• The attempt timeout must cover the body too. AttemptTimeout ends when the handler returns.\n" +
        "• You want to hedge. Attempts at the same time cannot share one HttpRequestMessage.\n" +
        "• You call the synchronous HttpClient.Send. The handler throws NotSupportedException.\n\n" +
        "[white]HttpClient.Timeout[/] sits outside the handler: it limits all the attempts and delays, and throws a " +
        "TaskCanceledException (section 7). Keep the factory's TotalTimeout shorter.")
    .Header("[white]When to wrap instead[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));

async Task<T> RunAsync<T>(string status, Func<Task<T>> call)
{
    log.Clear();
    clock.Restart();
    return await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(status, _ => call());
}

// Runs one call and reports the exception that reached the caller.
static async Task<(string Type, string Detail)> CatchAsync(Func<Task<HttpResponseMessage>> call)
{
    try
    {
        using HttpResponseMessage response = await call();
        return ("none", $"{(int)response.StatusCode}");
    }
    catch (RetryTimeoutException ex)
    {
        return (ex.GetType().Name, $"Timeout = {ex.Timeout.TotalMilliseconds:F0} ms");
    }
    catch (OperationCanceledException ex)
    {
        return (ex.GetType().Name, $"inner: {ex.InnerException?.GetType().Name ?? "none"}");
    }
}

void Show(string title, ScriptedHandler handler, string result, Color colour)
{
    AnsiConsole.Write(new Rule($"[deepskyblue1]{Markup.Escape(title)}[/]").LeftJustified());
    foreach (string line in log)
    {
        AnsiConsole.MarkupLine(line);
    }

    AnsiConsole.MarkupLine($"  The service saw [white]{handler.Calls.Count}[/] request(s): {string.Join(", ", handler.Calls)}");
    AnsiConsole.Write(new Panel(Markup.Escape(result)).Header("[white]The caller got[/]").Border(BoxBorder.Rounded).BorderColor(colour).Expand());
    AnsiConsole.WriteLine();
}

// A typed client, as IHttpClientFactory makes it: the HttpClient that it receives already has the RetryHandler inside.
internal sealed class OrdersClient(HttpClient http)
{
    public async Task<HttpStatusCode> PlaceOrderAsync(int sku, string idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "orders") { Content = new StringContent($$"""{ "sku": {{sku}} }""") };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using HttpResponseMessage response = await http.SendAsync(request);
        return response.StatusCode;
    }
}

// One scripted answer: a description for the log, and the response (or the hang) that the handler gives.
internal sealed record Step(string Description, Func<CancellationToken, Task<HttpResponseMessage>> Respond)
{
    public static Step Status(HttpStatusCode code) => new($"{(int)code}", _ => Task.FromResult(new HttpResponseMessage(code)));

    public static Step TooManyRequests(TimeSpan retryAfter) =>
        new($"429 (Retry-After: {retryAfter.TotalSeconds:F0})", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new(retryAfter) },
        }));

    public static Step Json(string json) =>
        new("200", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

    // The headers say 200, and the body fails while HttpClient reads it, after the handler has returned.
    public static Step BrokenBody() =>
        new("200, then the body broke", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() }));

    // Waits without answering until the token is cancelled, as a connection that hangs would.
    public static Step Hang() =>
        new("hung", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.OK);
        });
}

// A body that breaks part-way through. HttpClient wraps the IOException in an HttpRequestException.
internal sealed class BrokenContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        stream.Write("""{ "id": 42, "na"""u8);
        throw new IOException("The connection was reset while the body was being read.");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

// An HttpMessageHandler that plays a script, one step for each request. The last step repeats.
internal sealed class ScriptedHandler(params Step[] steps) : HttpMessageHandler
{
    public List<string> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Step step = steps[Math.Min(this.Calls.Count, steps.Length - 1)];
        this.Calls.Add($"{request.Method} {step.Description}");
        return step.Respond(cancellationToken);
    }
}
