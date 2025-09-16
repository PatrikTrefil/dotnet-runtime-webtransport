// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebTransport;

/// <summary>
/// Represents a WebTransport stream.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-1.2-3.8.1"/>
public abstract class WebTransportStream : Stream, IAsyncDisposable
{
    /// <summary>
    /// The stream ID of this stream.
    /// It is a 62-bit unsigned integer.
    /// </summary>
    public abstract long StreamId { get; }

    public WebTransportStreamType Type { get; }

    protected internal WebTransportStream(WebTransportStreamType type)
    {
        Debug.Assert(Enum.IsDefined(type));

        Type = type;
    }

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

    public MsQuicWebTransportStream(WebTransportStreamType type, ArrayBuffer arrayBuffer, QuicStream quicStream) : base(type)
    {
        ArgumentNullException.ThrowIfNull(quicStream);

        _readStream = new ConcatenatedStream(arrayBuffer, quicStream);
        _quicStream = quicStream;
        InitTcs(quicStream);
    }

    public MsQuicWebTransportStream(WebTransportStreamType type, QuicStream quicStream) : base(type)
    {
        ArgumentNullException.ThrowIfNull(quicStream);

        _quicStream = quicStream;
        _readStream = quicStream;
        InitTcs(quicStream);
    }

    private void InitTcs(QuicStream quicStream)
    {
        // TODO: log exceptions
        Task.Run(async () =>
        {
            try
            {
                await quicStream.ReadsClosed.ConfigureAwait(false);
                _tcsReadsClosed.SetResult();
            }
            catch (QuicException ex)
            {
                _tcsReadsClosed.SetException(QuicExceptionHandler(ex));
            }
        });
        Task.Run(async () =>
        {
            try
            {
                await quicStream.WritesClosed.ConfigureAwait(false);
                _tcsWritesClosed.SetResult();
            }
            catch (QuicException ex)
            {
                _tcsWritesClosed.SetException(QuicExceptionHandler(ex));
            }
        });
    }

    /// <summary>
    /// Send initial bytes containing session ID and stream type
    /// </summary>
    /// <returns></returns>
    internal async Task InitOutbound(ReadOnlyMemory<byte> encodedSessionId)
    {
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

    public override long Length => throw new NotSupportedException();

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
    internal void AbortQuicStream(QuicAbortDirection abortDirection, long httpErrorCode)
    {
        _quicStream.Abort(abortDirection, httpErrorCode);
    }

    public override void Abort(WebTransportAbortDirection abortDirection, long errorCode)
    {
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

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

    private static WebTransportException QuicExceptionHandler(QuicException quicException)
    {
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
                _readStream.Dispose();
                _quicStream.Dispose();
            }
        }

        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _quicStream.DisposeAsync().ConfigureAwait(false);
        await _readStream.DisposeAsync().ConfigureAwait(false);

        Dispose(false);

        GC.SuppressFinalize(this);
    }
}
