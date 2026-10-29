// <copyright file="Retriable.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using Corvus.Retry.Async;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    /// <summary>
    /// Methods to provde retry semantics for synchronous and asynchronous operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are three different ways of retrying tasks in this library.
    /// <list type="number">
    /// <item><see cref="RetryTask"/> and <see cref="RetryTask{T}"/> - starting a task to execute a function asynchronously, with retry</item>
    /// <item><see cref="Retriable"/> - wrapping a synchronous or asynchronous operation with retry semantics</item>
    /// <item><see cref="ReliableTaskRunner"/> -wrapping a long-running asynchronous operation in a host that ensures it continues to run even after failure. You can use <see cref="IRetryPolicy"/> to control when the task restarts.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class Retriable
    {
        /// <summary>
        /// Retries an operation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="func">The function to retry.</param>
        /// <returns>The result of the function.</returns>
        public static T Retry<T>(Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(func);

            return Retry(func, CancellationToken.None, new Count(10), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Retries an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="asyncFunc">The async function to retry.</param>
        /// <returns>A task which, when completes, provides the result of the function.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, false);
        }

        /// <summary>
        /// Retries an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="asyncFunc">The async function to retry.</param>
        /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context as per "await" semantics.</param>
        /// <returns>A task which, when completes, provides the result of the function.</returns>
        public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, bool continueOnCapturedContext)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, CancellationToken.None, new Count(10), AnyExceptionPolicy.Default, continueOnCapturedContext);
        }

        /// <summary>
        /// Retries an operation.
        /// </summary>
        /// <param name="func">The function to retry.</param>
        public static void Retry(Action func)
        {
            ArgumentNullException.ThrowIfNull(func);

            Retry(func, CancellationToken.None, new Count(10), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Retries an operation with cancellation.
        /// </summary>
        /// <param name="func">The function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        public static void Retry(Action func, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(func);

            Retry(func, cancellationToken, new Count(10), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Retries an asynchronous operation.
        /// </summary>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <returns>A Task which completes when the function is complete.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task RetryAsync(Func<Task> asyncFunc)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, false);
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <returns>A Task which completes when the function is complete.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, cancellationToken, new Count(10), AnyExceptionPolicy.Default);
        }

        /// <summary>
        /// Retries an asynchronous operation.
        /// </summary>
        /// <param name="asyncFunc">The async function to retry.</param>
        /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context as per "await" semantics.</param>
        /// <returns>A task which, when completes, provides the result of the function.</returns>
        public static Task RetryAsync(Func<Task> asyncFunc, bool continueOnCapturedContext)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, CancellationToken.None, new Count(10), AnyExceptionPolicy.Default, continueOnCapturedContext);
        }

        /// <summary>
        /// Retries an operation with cancellation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="func">The function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy to use.</param>
        /// <param name="policy">The retry policy to use.</param>
        /// <returns>The result of the function.</returns>
        public static T Retry<T>(Func<T> func, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(func);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            while (true)
            {
                try
                {
                    return func();
                }
                catch (Exception exception)
                {
                    TimeSpan delay = strategy.PrepareToRetry(exception);

                    if (!WillRetry(exception, cancellationToken, strategy, policy))
                    {
                        throw;
                    }

                    strategy.OnRetrying(new RetryEventArgs(exception, delay));

                    if (delay != TimeSpan.Zero)
                    {
                        SleepService.Instance.Sleep(delay);
                    }
                }
            }
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy to use.</param>
        /// <param name="policy">The retry policy to use.</param>
        /// <returns>A task which, when complete, provides the result of the function.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return RetryAsync(asyncFunc, cancellationToken, strategy, policy, false);
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <returns>A task which, when complete, provides the result of the function.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);

            return RetryAsync(asyncFunc, cancellationToken, new Count(10), AnyExceptionPolicy.Default, false);
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <typeparam name="T">The return type of the function to retry.</typeparam>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <param name="continueOnCapturedContext">Determines whether to continue on the captured context or not.</param>
        /// <returns>A task which, when complete, provides the result of the function.</returns>
        public static Task<T> RetryAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return RetryCoreAsync(asyncFunc, cancellationToken, strategy, policy, continueOnCapturedContext);
        }

        /// <summary>
        ///  Retries an operation with cancellation.
        /// </summary>
        /// <param name="func">The operatoin to retry.</param>
        /// <param name="cancellationToken">The token with which cancellation is signalled.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static void Retry(Action func, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(func);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            while (true)
            {
                try
                {
                    func();
                    return;
                }
                catch (Exception exception)
                {
                    TimeSpan delay = strategy.PrepareToRetry(exception);

                    if (!WillRetry(exception, cancellationToken, strategy, policy))
                    {
                        throw;
                    }

                    strategy.OnRetrying(new RetryEventArgs(exception, delay));

                    if (delay != TimeSpan.Zero)
                    {
                        SleepService.Instance.Sleep(delay);
                    }
                }
            }
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which to signal cancellation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <returns>A task which completes when the operation is complete.</returns>
        /// <remarks>This function does not continue on the captured context. See the overload if you want to override this default behaviour.</remarks>
        public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return RetryAsync(asyncFunc, cancellationToken, strategy, policy, false);
        }

        /// <summary>
        /// Retries an asynchronous operation with cancellation.
        /// </summary>
        /// <param name="asyncFunc">The asynchronous function to retry.</param>
        /// <param name="cancellationToken">The token with which to signal cancellation.</param>
        /// <param name="strategy">The retry strategy.</param>
        /// <param name="policy">The retry policy.</param>
        /// <param name="continueOnCapturedContext">Whether to continue on the captured context.</param>
        /// <returns>A task which completes when the operation completes.</returns>
        public static Task RetryAsync(Func<Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext)
        {
            ArgumentNullException.ThrowIfNull(asyncFunc);
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(policy);

            return RetryCoreAsync(asyncFunc, cancellationToken, strategy, policy, continueOnCapturedContext);
        }

        // The arguments are validated by the public overloads, so that a null argument throws before a task is created.
        // This is the same loop as RetryCoreAsync<T>. It is kept separate, rather than adapting the operation to return
        // a value, because the adapter would allocate a closure and a delegate on every call.
        private static async Task RetryCoreAsync(Func<Task> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext)
        {
            while (true)
            {
                TimeSpan delay;

                try
                {
                    await asyncFunc().ConfigureAwait(continueOnCapturedContext);
                    return;
                }
                catch (Exception exception)
                {
                    delay = strategy.PrepareToRetry(exception);

                    if (!WillRetry(exception, cancellationToken, strategy, policy))
                    {
                        // Rethrow from inside the catch block so that the exception keeps its original stack trace.
                        throw;
                    }

                    strategy.OnRetrying(new RetryEventArgs(exception, delay));
                }

                if (delay != TimeSpan.Zero)
                {
                    await SleepService.Instance.SleepAsync(delay).ConfigureAwait(continueOnCapturedContext);
                }
            }
        }

        // The arguments are validated by the public overloads, so that a null argument throws before a task is created.
        private static async Task<T> RetryCoreAsync<T>(Func<Task<T>> asyncFunc, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy, bool continueOnCapturedContext)
        {
            while (true)
            {
                TimeSpan delay;

                try
                {
                    return await asyncFunc().ConfigureAwait(continueOnCapturedContext);
                }
                catch (Exception exception)
                {
                    delay = strategy.PrepareToRetry(exception);

                    if (!WillRetry(exception, cancellationToken, strategy, policy))
                    {
                        // Rethrow from inside the catch block so that the exception keeps its original stack trace.
                        throw;
                    }

                    strategy.OnRetrying(new RetryEventArgs(exception, delay));
                }

                if (delay != TimeSpan.Zero)
                {
                    await SleepService.Instance.SleepAsync(delay).ConfigureAwait(continueOnCapturedContext);
                }
            }
        }

        private static bool WillRetry(Exception exception, CancellationToken cancellationToken, IRetryStrategy strategy, IRetryPolicy policy)
        {
            return strategy.CanRetry && !cancellationToken.IsCancellationRequested && policy.CanRetry(exception);
        }
    }
}