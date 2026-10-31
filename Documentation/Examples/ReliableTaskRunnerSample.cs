#:project ../../Solutions/Corvus.Retry/Corvus.Retry.csproj

// Corvus.Retry - ReliableTaskRunner sample
// Run from the repository root:  dotnet run Documentation/Examples/ReliableTaskRunnerSample.cs
// The narrative from the original Polyglot notebook is kept as comments above each section.

using System.Diagnostics;
using Corvus.Retry;
using Corvus.Retry.Policies;

// ### `ReliableTaskRunner`
//
// This is used to run a service-like operation that is supposed to execute 'forever' (or, at least, until
// cancellation). If the method fails, it should be re-started.
//
// You start a task using the static `ReliableTaskRunner.Run()` method. For example
//
//     ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => DoSomeOperationAsync(cancellationToken));
//
// When you want to terminate the task, you call the `StopAsync()` method e.g.
//
//     await runner.StopAsync();
//
// As with the other retry methods, there are overloads where you can pass an `IRetryPolicy` to control the restart
// behaviour.

// #### Example - Cancelling a non-ending operation
//
// Let's model an operation that needs to run forever, until it is explicitly cancelled.
//
// The method, `LongRunningProcess.ExecuteAsync()` (defined at the end of this file), has a while loop that throws an
// exception every ten times around. It runs until the cancellation token passed by the `ReliableTaskRunner` is
// cancelled.
//
// Let's run this in a `ReliableTaskRunner` and cancel the operation some time later using
// `ReliableTaskRunner.StopAsync()`.
//
// If we run this method and wrap it in a `ReliableTaskRunner` it will run forever, since the default policy is
// `AnyExceptionPolicy`, which will retry upon detecting any exception. We can stop it with the `StopAsync()` method.
Console.WriteLine("## Cancelling a non-ending operation");
{
    ReliableTaskRunner runner = ReliableTaskRunner.Run(cancellationToken => LongRunningProcess.ExecuteAsync(cancellationToken));
    await Task.Delay(5000); // Wait some time before cancelling operation
    Console.WriteLine("Stopping the runner");
    Task runnerTask = runner.StopAsync();

    // Wait for the operation to stop, then check whether the task returned is faulted. (The operation can fail just
    // as it is stopped, in which case it is not restarted and awaiting the task throws.)
    try
    {
        await runnerTask;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Awaiting the runner task threw {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine($"Is runner task faulted:\t{runnerTask.IsFaulted}");
    Console.WriteLine($"Runner task status:\t{runnerTask.Status}");
}

// We can see that the task returned has not faulted. This is because we interrupted the method with cancellation, and
// it returned normally, before it ever failed for good. If the method faults and the policy does not restart it, the
// `Task` returned by `StopAsync()` will be faulted, as the next example shows. This is because `ReliableTaskRunner` is
// supposed be used for operations that run forever.

// #### Example - cancelling an operation that faults
//
// Similar to the previous example, we have a method (now a member method of `ModelALongRunningProcess`, defined at the
// end of this file) with a `while (true)` loop that throws an exception every ten times around the loop, but this time
// we're recording the number of times it has been executed and the time since the last execution.
//
// Now, let's define a custom retry policy (`CustomRetryPolicy`) that stops retrying after
// `ExecuteALongRunningTransientProcess` has been executed 3 times.
//
// Now, let's run the method with `ReliableTaskRunner.Run()`, passing in a `CustomRetryPolicy` for the `policy`
// parameter.
//
// Then, every 5 seconds, we're going to check if the amount of time since the method was last executed is greater than
// 4 seconds, and if so, we'll assume that the method has faulted (it will have returned by then) and cancel the
// operation with `StopAsync()`.
//
// We expect to see `ExecuteALongRunningTransientProcess()` executed four times - the first three are followed by
// retries, the fourth is not. And the `Task` returned by `StopAsync()` will be faulted.
Console.WriteLine();
Console.WriteLine("## Cancelling an operation that faults");
{
    ModelALongRunningProcess modelALongRunningProcess = new();
    ReliableTaskRunner runner = ReliableTaskRunner.Run(
        cancellationToken => modelALongRunningProcess.ExecuteALongRunningTransientProcess(cancellationToken),
        new CustomRetryPolicy(modelALongRunningProcess));

    // If the time since the method was last executed is greater than 4s then assume the operation has faulted, exit
    // the loop and stop the operation.
    bool cannotCancel = true;
    while (cannotCancel)
    {
        int timeElapsedSinceLastMethodExecution = (int)modelALongRunningProcess.Stopwatch.Elapsed.TotalSeconds;
        if (timeElapsedSinceLastMethodExecution > 4)
        {
            cannotCancel = false;
        }
        else
        {
            await Task.Delay(5000); // Wait before checking again
        }
    }

    Console.WriteLine("Cancelling the operation");
    Task runnerTask = runner.StopAsync();

    // Wait for the operation to stop, then check whether the resulting task is faulted
    try
    {
        await runnerTask;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Awaiting the runner task threw {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine($"Is runner task faulted:\t{runnerTask.IsFaulted}");
}

// You can see from the output that after three retries, the policy stops retrying and the method has exited the while
// loop. At this point it's doing nothing - it has faulted.

// ---- Types used above (must follow the top-level statements) ----

// Models an operation that runs until it is cancelled, and fails every ten times around its loop.
public static class LongRunningProcess
{
    public static async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("LongRunningProcess.ExecuteAsync method executed");
        int counter = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100);
            int quotient = Math.DivRem(counter, 10, out int remainder);

            // Throw an exception every ten times around the loop
            if (remainder == 0 && quotient > 0)
            {
                Console.WriteLine("About to throw an exception");
                throw new InvalidOperationException("Transient failure");
            }

            counter++;
        }

        Console.WriteLine("Cancellation requested; LongRunningProcess.ExecuteAsync is returning");
    }
}

// Models an operation that fails every ten times around its loop, and records the number of times it has been
// executed and the time since the last execution.
public class ModelALongRunningProcess
{
    public Stopwatch Stopwatch { get; private set; } = new();

    public int ExecutionCount { get; private set; }

    public async Task ExecuteALongRunningTransientProcess(CancellationToken cancellationToken)
    {
        Console.WriteLine("ExecuteALongRunningTransientProcess method executed");
        this.ExecutionCount++;
        this.Stopwatch = Stopwatch.StartNew();
        int loopCounter = 0;
        while (true)
        {
            await Task.Delay(200);
            int quotient = Math.DivRem(loopCounter, 10, out int remainder);

            // Throw an exception every ten times around the loop
            if (remainder == 0 && quotient > 0)
            {
                Console.WriteLine("About to throw an exception");
                throw new InvalidOperationException("Transient failure");
            }

            loopCounter++;
        }
    }
}

// Stops retrying after ExecuteALongRunningTransientProcess has been executed 3 times.
public class CustomRetryPolicy : IRetryPolicy
{
    private readonly ModelALongRunningProcess modelALongRunningProcess;

    public CustomRetryPolicy(ModelALongRunningProcess modelALongRunningProcess)
    {
        this.modelALongRunningProcess = modelALongRunningProcess;
    }

    public bool CanRetry(Exception exception)
    {
        return this.modelALongRunningProcess.ExecutionCount <= 3;
    }
}
