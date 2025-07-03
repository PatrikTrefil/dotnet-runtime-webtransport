// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Net.Quic;
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
        Type = type;
    }

    /// <summary>
    /// Aborts either the reading, writing, or both sides of the stream.
    /// </summary>
    /// <param name="abortDirection">The direction of the stream to abort.</param>
    /// <param name="errorCode">The error code with which to abort the stream.</param>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    public abstract void Abort(WebTransportAbortDirection abortDirection, int errorCode);
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

    public MsQuicWebTransportStream(WebTransportStreamType type, ArrayBuffer arrayBuffer, QuicStream quicStream) : base(type)
    {
        _readStream = new ConcatenatedStream(arrayBuffer, quicStream);
        _quicStream = quicStream;
    }
    public MsQuicWebTransportStream(WebTransportStreamType type, QuicStream quicStream) : base(type)
    {
        _quicStream = quicStream;
        _readStream = quicStream;
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
            _ => throw new WebTransportException("Unknown stream type")
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

    private static QuicAbortDirection WebTransportAbortDirectionToQuicAbortDirection(WebTransportAbortDirection abortDirection)
    {
        return abortDirection switch
        {
            WebTransportAbortDirection.Read => QuicAbortDirection.Read,
            WebTransportAbortDirection.Write => QuicAbortDirection.Write,
            WebTransportAbortDirection.Both => QuicAbortDirection.Both,
            _ => throw new ArgumentOutOfRangeException(nameof(abortDirection), abortDirection, "Invalid abort direction.")
        };
    }
    public override void Abort(WebTransportAbortDirection abortDirection, int errorCode)
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
            throw new InvalidOperationException("This stream does not support writing");
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

    private static WebTransportException QuicExceptionHandler(QuicException quicException)
    {
        if (quicException.ApplicationErrorCode is long applicationErrorCode)
        {
            int remappedErrorCode;
            try
            {
                remappedErrorCode = ErrorCodeRemapping.HttpCodeToWebTransportCode(applicationErrorCode);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new WebTransportException("Invalid application error code received.");
            }
            return new WebTransportStreamClosedException("The stream has beed closed", remappedErrorCode, quicException);
        }
        else
        {
            return new WebTransportException("Transport layer error occurred.", quicException);
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
