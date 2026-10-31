// <copyright file="TrackingContent.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System.Net.Http;

/// <summary>
/// A <see cref="StringContent"/> that records whether it was disposed. Disposing an <see cref="HttpResponseMessage"/>
/// disposes its content, so a test can tell whether a response was disposed.
/// </summary>
internal sealed class TrackingContent(string body) : StringContent(body)
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        this.Disposed = true;
        base.Dispose(disposing);
    }
}
