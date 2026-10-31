// <copyright file="TestContents.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry.Http;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Content types for the bodies of responses and requests in the handler tests.
/// </summary>
internal static class TestContents
{
    /// <summary>
    /// A response content that fails with an <see cref="IOException"/> when it is read, as a connection that breaks
    /// while the body downloads does.
    /// </summary>
    internal sealed class FailingContent : HttpContent
    {
        public int Reads { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            this.Reads++;
            return Task.FromException(new IOException("The connection broke while the body downloaded."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A response content whose body arrives only when the test calls <see cref="Complete"/>.
    /// </summary>
    internal sealed class PendingContent : HttpContent
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource body = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a task that completes when something starts to read the body.
        /// </summary>
        public Task Started => this.started.Task;

        public void Complete() => this.body.TrySetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            this.started.TrySetResult();
            await this.body.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("the body"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return this.SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A stream that cannot seek, as a network or a pipe stream cannot. A <see cref="StreamContent"/> over it can be
    /// sent only once.
    /// </summary>
    internal sealed class NonSeekableStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
