// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebTransport;

/// <summary>
/// Represents a WebTransport stream.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-1.2-3.8.1"/>
public abstract class WebTransportStream : Stream
{
    /// <summary>
    /// The identifier of this stream.
    /// </summary>
    /// <value>It is a 62-bit unsigned integer.</value>
    public abstract long StreamId { get; }

    /// <summary>
    /// Gets the stream type.
    /// </summary>
    public WebTransportStreamType Type { get; }


    protected internal WebTransportStream(WebTransportStreamType type)
    {
        Debug.Assert(Enum.IsDefined(type));

        Type = type;
    }

    /// <summary>
    /// Gracefully completes the writing side of the stream.
    /// </summary>
    /// <remarks>
    /// Equivalent to using <see cref="WriteAsync(ReadOnlyMemory{byte}, bool, CancellationToken)"/> with <c>completeWrites: true</c>.
    /// </remarks>
    public abstract void CompleteWrites();

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteAsync(buffer, completeWrites: false, cancellationToken);

    /// <summary>
    /// Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The region of memory to write data from.</param>
    /// <param name="completeWrites"><c>true</c> to notify the peer about gracefully closing the write side; otherwise, <c>false</c>.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts either the reading, writing, or both sides of the stream.
    /// </summary>
    /// <param name="abortDirection">The direction of the stream to abort.</param>
    /// <param name="errorCode">The error code with which to abort the stream. The value must be in the range [0, 2^32).</param>
    /// <exception cref="ArgumentOutOfRangeException">When the <paramref name="errorCode"/> is not in the range [0, 2^32).</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    public abstract void Abort(WebTransportAbortDirection abortDirection, long errorCode);

    /// <summary>
    /// Gets a <see cref="Task"/> that will complete once the reading side has been closed (gracefully or abortively).
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.3-11.4.1"/>
    public abstract Task ReadsClosed { get; }

    /// <summary>
    /// Gets a <see cref="Task"/> that will complete once the writing side has been closed (gracefully or abortively).
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.3-11.2.1"/>
    public abstract Task WritesClosed { get; }

    protected override void Dispose(bool disposing)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        base.Dispose(disposing);
    }

    /// <summary>
    /// If the read side is not fully consumed, i.e.: <see cref="ReadsClosed"/> is not completed and/or <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> hasn't returned <c>0</c>,
    /// dispose will abort the read side with provided <see cref="QuicConnectionOptions.DefaultStreamErrorCode"/>.
    /// If the write side hasn't been closed, it'll be closed gracefully as if <see cref="CompleteWrites"/> was called.
    /// Finally, all resources associated with the stream will be released.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public sealed override async ValueTask DisposeAsync()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        await DisposeAsyncCore().ConfigureAwait(false);

        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
