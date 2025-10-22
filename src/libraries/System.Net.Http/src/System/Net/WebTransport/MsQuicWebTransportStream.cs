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
    internal async Task InitOutbound(ReadOnlyMemory<byte> encodedSessionId, CancellationToken cancellationToken)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ReadOnlyMemory<byte> initialBytes = Type switch
        {
            WebTransportStreamType.Unidirectional => s_unidirectionalStreamTypeEncodedAsVariableLengthInteger,
            WebTransportStreamType.Bidirectional => s_bidirectionalStreamTypeEncodedAsVariableLengthInteger,
            _ => throw new WebTransportException(WebTransportError.InternalError, SR.net_webtransport_internal_error)
        };
        await _readStream.WriteAsync(initialBytes, cancellationToken).ConfigureAwait(false);
        await _readStream.WriteAsync(encodedSessionId, cancellationToken).ConfigureAwait(false);
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
            _ => throw new ArgumentOutOfRangeException(paramName, abortDirection, SR.net_webtransport_stream_invalid_abort_direction)
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

    #region Reads

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        try
        {
            return _readStream.BeginRead(buffer, offset, count, callback, state);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        try
        {
            return _readStream.EndRead(asyncResult);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _readStream.Read(buffer, offset, count);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override int ReadByte()
    {
        try
        {
            return _readStream.ReadByte();
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        try
        {
            return _readStream.ReadAsync(buffer, offset, count, cancellationToken);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return _readStream.Read(buffer);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return await _readStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    #endregion

    #region Writes

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        try
        {
            return _quicStream.BeginWrite(buffer, offset, count, callback, state);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        try
        {
            _quicStream.EndWrite(asyncResult);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override void WriteByte(byte value)
    {
        try
        {
            _quicStream.WriteByte(value);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        try
        {
            await _quicStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _quicStream.Write(buffer);
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            _quicStream.Write(buffer, offset, count);
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken cancellationToken = default)
    {
        try
        {
            await _quicStream.WriteAsync(buffer, completeWrites, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw QuicExceptionHandler(ex);
        }
    }

    #endregion

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            _quicStream.Flush();
        }
        catch (QuicException quicException)
        {
            throw QuicExceptionHandler(quicException);
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            await _quicStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            catch (ArgumentOutOfRangeException ex)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

                return new WebTransportException(WebTransportError.StreamAborted, null, null, SR.Format(SR.net_webtransport_stream_invalid_application_error_code, applicationErrorCode));
            }
            return new WebTransportException(WebTransportError.StreamAborted, remappedErrorCode, null, SR.net_webtransport_stream_aborted, quicException);
        }
        else if (quicException.QuicError == QuicError.OperationAborted)
        {
            return new WebTransportException(WebTransportError.OperationAborted, SR.net_webtransport_operation_aborted, quicException);
        }
        else
        {
            return new WebTransportException(WebTransportError.TransportLayerError, SR.net_webtransport_transport_layer_error, quicException);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                _quicStream.Abort(QuicAbortDirection.Both, DefaultStreamErrorCode);
                _readStream.Dispose();
                _quicStream.Dispose();
            }
        }

        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        _quicStream.Abort(QuicAbortDirection.Both, DefaultStreamErrorCode);
        await _quicStream.DisposeAsync().ConfigureAwait(false);
        await _readStream.DisposeAsync().ConfigureAwait(false);
    }
}
