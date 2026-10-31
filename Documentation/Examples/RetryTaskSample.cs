#:project ../../Solutions/Corvus.Retry/Corvus.Retry.csproj

// Corvus.Retry - RetryTask sample
// Run from the repository root:  dotnet run Documentation/Examples/RetryTaskSample.cs
// The narrative from the original Polyglot notebook is kept as comments above each section.
//
// The operations in this sample fail at random, so a retry can still give up after its last try. The sample catches
// and prints those failures, so that every example runs and the app exits normally.

using System.Diagnostics;
using Corvus.Retry;
using Corvus.Retry.Policies;
using Corvus.Retry.Strategies;

// ## `RetryTask`
//
// The `RetryTask` is an analog of `Task`
// (https://learn.microsoft.com/dotnet/api/system.threading.tasks.task) which has built-in retry semantics.
//
// Instead of calling `Task.Factory.StartNew()` you can call `RetryTask.Factory.StartNew()` with all the familiar
// parameters.
//
// In addition to those usual parameters, you can pass an `IRetryPolicy` and an `IRetryStrategy` to control the retry
// behaviour. If you do not pass them, the defaults are `new Count()` (5 tries) and `AnyExceptionPolicy`. These differ
// from the `Retriable` default strategy, which is `new Count(10)`.
//
// (See RetriableSample.cs in this folder for examples of `Retriable`, `IRetryPolicy` and `IRetryStrategy`.)
//
// ### Example
//
// This basic example uses `RetryTask.Factory.StartNew()` just like you could use `Task.Factory.StartNew()`: to execute
// multiple operations concurrently.
//
// We have a `MockClient` class (defined at the end of this file) with a single method,
// `DoAnOperationThatFailsTransiently()`, that fails transiently - it throws a `MockClientException` with 80%
// probability. There's also a `RetryOnMockClientExceptionPolicy`, which is a custom implementation of `IRetryPolicy`
// that will trigger a retry when a `MockClientException` is detected.
//
// Now, let's call `DoAnOperationThatFailsTransiently()` three times via `RetryTask.Factory.StartNew()`. We should see
// the method being retried a number of times.
Console.WriteLine("## RetryTask.Factory.StartNew - concurrent operations");
{
    Task task1 = RetryTask.Factory.StartNew(() => MockClient.DoAnOperationThatFailsTransiently(), new Count(5), new RetryOnMockClientExceptionPolicy());
    await Task.Delay(200);
    Task task2 = RetryTask.Factory.StartNew(() => MockClient.DoAnOperationThatFailsTransiently(), new Count(5), new RetryOnMockClientExceptionPolicy());
    await Task.Delay(200);
    Task task3 = RetryTask.Factory.StartNew(() => MockClient.DoAnOperationThatFailsTransiently(), new Count(5), new RetryOnMockClientExceptionPolicy());

    await WaitAndReportAsync(task1, task2, task3);
}

// ### Example - mock HTTP service
//
// Let's imagine we were consuming an HttpService which occasionally gives us a `429 - Too Many Requests` error in an
// `HttpServiceException`.
//
// If a client only offers synchronous APIs we can start a `Task` to make the calls to them asynchronous, so we can do
// other work whilst they're processing, and have them retry when a `429 - Too Many Requests` exception is detected.
//
// Let's define a `MockHttpServiceException`, a `MockHttpService`, and a custom retry policy that will trigger a retry
// when a `429 - Too Many Requests` exception is detected (all defined at the end of this file).
//
// Now, let's use `RetryTask.Factory.StartNew()` to call `MakeLotsOfRequests` and have it retry according to
// `RetryOnTooManyRequestsPolicy`, and do work on another thread whilst that retry process is happening.
// `MakeLotsOfRequests` always fails, so the task faults after its fifth try.
Console.WriteLine();
Console.WriteLine("## RetryTask.Factory.StartNew - doing other work while retrying");
{
    MockHttpService mockHttpService = new();
    Task retryTask = RetryTask.Factory.StartNew(() => mockHttpService.MakeLotsOfRequests(), CancellationToken.None, strategy: new Count(5), policy: new RetryOnTooManyRequestsPolicy());
    DoSomeWork();
    await WaitAndReportAsync(retryTask);
}

// ### Example - Run long running transient operations concurrently with retry behaviour
//
// We have a class `ModelALongRunningProcess` (defined at the end of this file) with a method
// `ExecuteALongRunningTransientProcess()` that models a long running transient operation. The probability of the
// method throwing an exception starts at 1 and decreases exponentially with the number of method executions. It also
// always throws during its first 5 seconds.
//
// This example is similar to the first in this sample in that it demonstrates how you can execute operations in
// parallel with retry behaviour using `RetryTask.Factory.StartNew()`. However, in this case we're using the generic
// version, `RetryTask<T>.Factory.StartNew()`, since the method returns a value, resulting in a `Task<T>` that
// eventually returns a value.
//
// Let's create three `ModelALongRunningProcess` instances and run the `ExecuteALongRunningTransientProcess()` method on
// each of them concurrently using `RetryTask<int>.Factory.StartNew()`. We allow up to 10 tries, so that each operation
// has time to get past its first 5 seconds and is very likely to succeed.
Console.WriteLine();
Console.WriteLine("## RetryTask<T>.Factory.StartNew - long running operations returning a value");
{
    ModelALongRunningProcess modelALongRunningProcess1 = new("1");
    ModelALongRunningProcess modelALongRunningProcess2 = new("2");
    ModelALongRunningProcess modelALongRunningProcess3 = new("3");

    Task<int> task1 = RetryTask<int>.Factory.StartNew(() => modelALongRunningProcess1.ExecuteALongRunningTransientProcess(), new Count(10), new AnyExceptionPolicy());
    await Task.Delay(200);
    Task<int> task2 = RetryTask<int>.Factory.StartNew(() => modelALongRunningProcess2.ExecuteALongRunningTransientProcess(), new Count(10), new AnyExceptionPolicy());
    await Task.Delay(200);
    Task<int> task3 = RetryTask<int>.Factory.StartNew(() => modelALongRunningProcess3.ExecuteALongRunningTransientProcess(), new Count(10), new AnyExceptionPolicy());

    Task<int>[] tasks = [task1, task2, task3];
    await WaitAndReportAsync(tasks);

    for (int i = 0; i < tasks.Length; i++)
    {
        if (tasks[i].IsCompletedSuccessfully)
        {
            Console.WriteLine($"Process {i + 1} succeeded after {tasks[i].Result}s");
        }
    }
}

// Simulates other work done on this thread while the retry tasks run.
static void DoSomeWork()
{
    for (int i = 0; i < 5; i++)
    {
        Thread.Sleep(500);
        Console.WriteLine("Doing some other work");
    }
}

// Waits for all the tasks to finish, then prints the outcome of each one. A task faults if its retry gives up.
static async Task WaitAndReportAsync(params Task[] tasks)
{
    try
    {
        await Task.WhenAll(tasks);
    }
    catch
    {
        // Reported per task below.
    }

    for (int i = 0; i < tasks.Length; i++)
    {
        Task task = tasks[i];
        string outcome = task.IsFaulted
            ? $"Faulted - the retry gave up. {task.Exception!.InnerException?.GetType().Name}: {task.Exception.InnerException?.Message}"
            : task.Status.ToString();
        Console.WriteLine($"Task {i + 1}: {outcome}");
    }
}

// ---- Types used above (must follow the top-level statements) ----

// The exception thrown by the mock client.
public class MockClientException : Exception
{
    public MockClientException(string message)
        : base(message)
    {
    }
}

// A mock client whose operation fails 80% of the time.
public static class MockClient
{
    public static void DoAnOperationThatFailsTransiently()
    {
        Console.WriteLine("method DoAnOperationThatFailsTransiently() called");
        Thread.Sleep(500);
        int randomNum = Random.Shared.Next(1, 11);
        if (randomNum < 9)
        {
            throw new MockClientException(message: "Too many requests");
        }

        Console.WriteLine("Operation succeeded");
    }
}

// Retries only when a MockClientException is detected.
public class RetryOnMockClientExceptionPolicy : IRetryPolicy
{
    public bool CanRetry(Exception exception)
    {
        return exception is MockClientException;
    }
}

// The exception thrown by the mock HTTP service.
public class MockHttpServiceException : Exception
{
    public MockHttpServiceException(string message, string statusCode)
        : base(message)
    {
        this.StatusCode = statusCode;
    }

    public string StatusCode { get; }
}

// A mock HTTP service with synchronous methods that always fail.
public class MockHttpService
{
    public void MakeLotsOfRequests()
    {
        Console.WriteLine("MakeLotsOfRequests() method called");
        Thread.Sleep(1000);
        throw new MockHttpServiceException("429 - Too Many Requests", "429");
    }

    public void MakeRequestThatWillTimeOut()
    {
        Console.WriteLine("MakeRequestThatWillTimeOut() method called");
        throw new MockHttpServiceException("408 - Request timed out", "408");
    }
}

// Retries only when the mock HTTP service reports "429 - Too Many Requests".
public class RetryOnTooManyRequestsPolicy : IRetryPolicy
{
    public bool CanRetry(Exception exception)
    {
        return exception is MockHttpServiceException httpException && httpException.StatusCode == "429";
    }
}

// Models a long running operation that always fails during its first 5 seconds, and after that fails with a
// probability that decreases exponentially with the number of executions.
public class ModelALongRunningProcess
{
    private const int MaxLimit = 1000;
    private const int MinRunTimeBeforeNotThrowing = 5;

    private readonly Stopwatch stopwatch = new();
    private int methodExecutionCount;

    public ModelALongRunningProcess(string name)
    {
        this.Name = name;
    }

    public string Name { get; }

    public int ExecuteALongRunningTransientProcess()
    {
        if (this.methodExecutionCount == 0)
        {
            this.stopwatch.Start();
        }

        Console.WriteLine($"ExecuteALongRunningTransientProcess{this.Name} method executed");
        this.methodExecutionCount++;

        // Wait some time to simulate long process
        Thread.Sleep(1000);

        // Decide whether to throw an error
        bool shouldThrowError = this.ShouldThrowError();
        Console.WriteLine($"{this.Name}:\tShould throw error: {shouldThrowError}");
        if (shouldThrowError)
        {
            throw new InvalidOperationException($"Process {this.Name} failed");
        }

        return (int)this.stopwatch.Elapsed.TotalSeconds;
    }

    // Probability of throwing an error decreases from 1 exponentially with methodExecutionCount
    private bool ShouldThrowError()
    {
        int comparison = (int)(1 / Math.Pow(1.1, this.methodExecutionCount) * MaxLimit);
        int randomNum = Random.Shared.Next(1, MaxLimit + 1);
        return randomNum <= comparison || this.stopwatch.Elapsed.TotalSeconds < MinRunTimeBeforeNotThrowing;
    }
}
