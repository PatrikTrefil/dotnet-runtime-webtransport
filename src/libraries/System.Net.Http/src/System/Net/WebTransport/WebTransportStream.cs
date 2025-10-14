// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Quic;
using System.Runtime.CompilerServices;
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

    protected long defaultStreamErrorCode;

    protected internal WebTransportStream(WebTransportStreamType type, long defaultStreamErrorCode)
    {
        Debug.Assert(Enum.IsDefined(type));

        Type = type;
        this.defaultStreamErrorCode = defaultStreamErrorCode;
    }

    /// <summary>
    /// Gracefully completes the writing side of the stream.
    /// </summary>
    /// <remarks>
    /// Equivalent to using <see cref="WriteAsync(ReadOnlyMemory{byte}, bool, CancellationToken)"/> with <c>completeWrites: true</c>.
    /// </remarks>
    public abstract void CompleteWrites();

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

// TODO: add session data limit tracking

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
internal sealed class MsQuicWebTransportStream : WebTransportStream
{
    private readonly QuicStream _quicStream;
    private readonly Stream _readStream;
    private bool _isDisposed;
    private static readonly ReadOnlyMemory<byte> s_bidirectionalStreamTypeEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x41 };
    private static readonly ReadOnlyMemory<byte> s_unidirectionalStreamTypeEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x54 };
    private readonly TaskCompletionSource _tcsReadsClosed = new();
    private readonly TaskCompletionSource _tcsWritesClosed = new();

    /// <summary>
    /// Create inbound stream
    /// </summary>
    private MsQuicWebTransportStream(WebTransportStreamType type, Stream readStream, QuicStream quicStream, long defaultStreamErrorCode) : base(type, defaultStreamErrorCode)
    {
        ArgumentNullException.ThrowIfNull(quicStream);
        ArgumentNullException.ThrowIfNull(readStream);

        _readStream = readStream;
        _quicStream = quicStream;

        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.Associate(this, quicStream);
        }

        if (type == WebTransportStreamType.Unidirectional)
        {
            _tcsWritesClosed.SetResult();
        }
        else
        {
            ReactToWritesClosedInQuicStream();
        }

        ReactToReadsClosedInQuicStream();
    }

    /// <summary>
    /// Create outbound stream
    /// </summary>
    private MsQuicWebTransportStream(WebTransportStreamType type, QuicStream quicStream, long defaultStreamErrorCode) : base(type, defaultStreamErrorCode)
    {
        ArgumentNullException.ThrowIfNull(quicStream);

        _readStream = quicStream;
        _quicStream = quicStream;

        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.Associate(this, quicStream);
        }

        if (type == WebTransportStreamType.Unidirectional)
        {
            _tcsReadsClosed.SetResult();
        }
        else
        {
            ReactToReadsClosedInQuicStream();
        }

        ReactToWritesClosedInQuicStream();
    }


    public static MsQuicWebTransportStream CreateInboundStream(WebTransportStreamType type, ArrayBuffer arrayBuffer, QuicStream quicStream, long defaultStreamErrorCode)
        => new MsQuicWebTransportStream(type, new ConcatenatedStream(arrayBuffer, quicStream), quicStream, defaultStreamErrorCode);
    public static MsQuicWebTransportStream CreateOutboundStream(WebTransportStreamType type, QuicStream quicStream, long defaultStreamErrorCode)
        => new MsQuicWebTransportStream(type, quicStream, defaultStreamErrorCode);

    private void ReactToWritesClosedInQuicStream()
    {
        Task.Run(async () =>
        {
            try
            {
                await _quicStream.WritesClosed.ConfigureAwait(false);
                _tcsWritesClosed.SetResult();
            }
            catch (QuicException ex)
            {
                _tcsWritesClosed.SetException(QuicExceptionHandler(ex));
            }
        });
    }

    private void ReactToReadsClosedInQuicStream()
    {
        Task.Run(async () =>
        {
            try
            {
                await _quicStream.ReadsClosed.ConfigureAwait(false);
                _tcsReadsClosed.SetResult();
            }
            catch (QuicException ex)
            {
                _tcsReadsClosed.SetException(QuicExceptionHandler(ex));
            }
        });
    }

    /// <summary>
    /// Send initial bytes containing session ID and stream type
    /// </summary>
    /// <returns></returns>
    internal async Task InitOutbound(ReadOnlyMemory<byte> encodedSessionId)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ReadOnlyMemory<byte> initialBytes = Type switch
        {
            WebTransportStreamType.Unidirectional => s_unidirectionalStreamTypeEncodedAsVariableLengthInteger,
            WebTransportStreamType.Bidirectional => s_bidirectionalStreamTypeEncodedAsVariableLengthInteger,
            _ => throw new WebTransportException(WebTransportError.InternalError, "Unknown stream type.")
        };
        await _readStream.WriteAsync(initialBytes).ConfigureAwait(false);
        await _readStream.WriteAsync(encodedSessionId).ConfigureAwait(false);
    }

    public override long StreamId => _quicStream.Id;

    public override bool CanRead => !_isDisposed && _readStream.CanRead;

    public override bool CanSeek => !_isDisposed && _readStream.CanSeek;

    public override bool CanWrite => !_isDisposed && _quicStream.CanWrite;

    /// <summary>
    /// Gets the length of the data available on the stream. This property is not currently supported and always throws a <see cref="NotSupportedException"/>.
    /// </summary>
    /// <value>A long value representing the length of the stream in bytes.</value>
    /// <exception cref="NotSupportedException">In all cases.</exception>
    public override long Length => throw new NotSupportedException();

    /// <summary>
    /// Gets or sets the position within the current stream. This property is not currently supported and always throws a <see cref="NotSupportedException"/>.
    /// </summary>
    /// <value>The current position within the stream.</value>
    /// <exception cref="NotSupportedException">In all cases.</exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override Task ReadsClosed => _tcsReadsClosed.Task;

    public override Task WritesClosed => _tcsWritesClosed.Task;

    private static QuicAbortDirection WebTransportAbortDirectionToQuicAbortDirection(WebTransportAbortDirection abortDirection, [CallerArgumentExpression(nameof(abortDirection))] string? paramName = null)
    {
        return abortDirection switch
        {
            WebTransportAbortDirection.Read => QuicAbortDirection.Read,
            WebTransportAbortDirection.Write => QuicAbortDirection.Write,
            WebTransportAbortDirection.Both => QuicAbortDirection.Both,
            _ => throw new ArgumentOutOfRangeException(paramName, abortDirection, "Invalid abort direction value.")
        };
    }

    /// <summary>
    /// Abort the underlying QUIC stream. Used to abort the stream with error codes outside of the WebTransport error code range.
    /// </summary>
    internal void AbortQuicStream(QuicAbortDirection abortDirection, Http3ErrorCode httpErrorCode)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        _quicStream.Abort(abortDirection, (long)httpErrorCode);
    }

    public override void Abort(WebTransportAbortDirection abortDirection, long errorCode)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicAbortDirection quicAbortDirection = WebTransportAbortDirectionToQuicAbortDirection(abortDirection);
        long remappedErrorCode = ErrorCodeRemapping.WebTransportCodeToHttpCode(errorCode);
        try
        {
            _quicStream.Abort(quicAbortDirection, remappedErrorCode);
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    public override void CompleteWrites()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _quicStream.CompleteWrites();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!CanWrite)
        {
            throw new NotSupportedException("This stream does not support writing");
        }

        try
        {
            await _quicStream.WriteAsync(buffer, completeWrites, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException e)
        {
            QuicExceptionHandler(e);
        }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!CanWrite)
        {
            throw new NotSupportedException("Flush is not supported, because the stream does not support writing.");
        }

        try
        {
            _readStream.Flush();
        }
        catch (QuicException quicException)
        {
            QuicExceptionHandler(quicException);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return _readStream.Read(buffer, offset, count);
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    /// <summary>
    /// Sets the current position of the stream to the given value. This method is not currently supported and always throws a <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="offset">A byte offset relative to the <paramref name="origin"/> parameter.</param>
    /// <param name="origin">A value of type <see cref="SeekOrigin"/> indicating the reference point used to obtain the new position.</param>
    /// <returns>The new position within the current stream.</returns>
    /// <exception cref="NotSupportedException">In all cases.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Sets the length of the stream. This method is not currently supported and always throws a <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="value">The desired length of the current stream in bytes.</param>
    /// <exception cref="NotSupportedException">In all cases.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!CanWrite)
        {
            throw new NotSupportedException("This stream does not support writing");
        }

        try
        {
            _quicStream.Write(buffer, offset, count);
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    private WebTransportException QuicExceptionHandler(QuicException quicException)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, quicException);

        if (quicException.QuicError == QuicError.StreamAborted)
        {
            long applicationErrorCode = (long)quicException.ApplicationErrorCode!; // can't be null if QuicError is StreamAborted

            long remappedErrorCode;
            try
            {
                remappedErrorCode = ErrorCodeRemapping.HttpCodeToWebTransportCode(applicationErrorCode);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new WebTransportException(WebTransportError.StreamAborted, null, null, "Stream aborted with invalid application error code.");
            }
            return new WebTransportException(WebTransportError.StreamAborted, remappedErrorCode, null, "The stream has beed aborted.", quicException);
        }
        else
        {
            return new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error occurred.", quicException);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                _quicStream.Abort(QuicAbortDirection.Both, defaultStreamErrorCode);
                _readStream.Dispose();
                _quicStream.Dispose();
            }
        }

        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        _quicStream.Abort(QuicAbortDirection.Both, defaultStreamErrorCode);
        await _quicStream.DisposeAsync().ConfigureAwait(false);
        await _readStream.DisposeAsync().ConfigureAwait(false);
    }
}
