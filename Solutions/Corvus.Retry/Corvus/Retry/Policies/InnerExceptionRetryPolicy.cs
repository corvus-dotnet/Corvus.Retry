// <copyright file="InnerExceptionRetryPolicy.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Policies;

using System;
using System.Collections.ObjectModel;

/// <summary>
/// A <see cref="IRetryPolicy"/> that retries when the exception, or an exception inside it, matches.
/// </summary>
/// <remarks>Created by <see cref="RetryPolicies.HandleInner{TException}(Func{TException, bool}?)"/>.</remarks>
internal sealed class InnerExceptionRetryPolicy : IRetryPolicy
{
    private readonly Func<Exception, bool> match;

    /// <summary>
    /// Initializes a new instance of the <see cref="InnerExceptionRetryPolicy"/> class.
    /// </summary>
    /// <param name="match">The function that returns true for an exception that can be retried.</param>
    internal InnerExceptionRetryPolicy(Func<Exception, bool> match)
    {
        this.match = match;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This examines the exception, its <see cref="Exception.InnerException"/> chain, and the
    /// <see cref="AggregateException.InnerExceptions"/> of an <see cref="AggregateException"/>, depth first.
    /// </remarks>
    public bool CanRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return this.Search(exception);
    }

    private bool Search(Exception exception)
    {
        // Plain loops and recursion rather than iterators or LINQ, so that deciding allocates nothing.
        while (true)
        {
            if (this.match(exception))
            {
                return true;
            }

            if (exception is AggregateException aggregate)
            {
                ReadOnlyCollection<Exception> inner = aggregate.InnerExceptions;
                for (int i = 0; i < inner.Count; i++)
                {
                    if (this.Search(inner[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (exception.InnerException is not Exception next)
            {
                return false;
            }

            exception = next;
        }
    }
}
