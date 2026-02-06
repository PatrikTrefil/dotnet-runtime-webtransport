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
/// Implementation that uses System.Net.Quic
/// </summary>
internal sealed class MsQuicWebTransportStream(WebTransportStreamType type, long defaultStreamErrorCode, Stream readStream, QuicStream quicStream, Action<long> addBytesSent) : WebTransportStream(type)
{
    private readonly QuicStream _quicStream = quicStream ?? throw new ArgumentNullException(nameof(quicStream));
    private readonly Stream _readStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
    private bool _isDisposed;

    private static readonly ReadOnlyMemory<byte> s_bidirectionalStreamTypeEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x41 };
    private static readonly ReadOnlyMemory<byte> s_unidirectionalStreamTypeEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x54 };

    private readonly TaskCompletionSource _tcsReadsClosed = new();
    private readonly TaskCompletionSource _tcsWritesClosed = new();

    private readonly Action<long> _addBytesSent = addBytesSent;
    /// <summary>
    /// <see cref="QuicStream.Abort(QuicAbortDirection, long)"/> and <see cref="QuicStream.DisposeAsync"/> and <see cref="QuicStream.Dispose(bool)"/> may not run at the same time.
    /// To prevent this we use this lock.
    /// </summary>
    private readonly Lock _abortDisposeLock = new();

    /// <summary>
    /// Error code used when the stream needs to abort read or write side of the stream internally, e.g. in <see cref="WebTransportStream.DisposeAsync()"/>.
    /// </summary>
    private readonly long _remappedDefaultStreamErrorCode = ErrorCodeRemapping.WebTransportCodeToHttpCode(defaultStreamErrorCode);

    /// <summary>
    /// Create inbound stream
    /// </summary>
    private MsQuicWebTransportStream(WebTransportStreamType type, Stream readStream, QuicStream quicStream, long defaultStreamErrorCode, Action<long> addBytesSent) : this(type, defaultStreamErrorCode, readStream, quicStream, addBytesSent)
    {
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
    private MsQuicWebTransportStream(WebTransportStreamType type, QuicStream quicStream, long defaultStreamErrorCode, Action<long> addBytesSent) : this(type, defaultStreamErrorCode, readStream: quicStream, quicStream, addBytesSent)
    {
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


    public static MsQuicWebTransportStream CreateInboundStream(WebTransportStreamType type, ArrayBuffer arrayBuffer, QuicStream quicStream, long defaultStreamErrorCode, Action<long> addBytesSent)
        => new MsQuicWebTransportStream(type, new ConcatenatedStream(arrayBuffer, quicStream), quicStream, defaultStreamErrorCode, addBytesSent);
    public static MsQuicWebTransportStream CreateOutboundStream(WebTransportStreamType type, QuicStream quicStream, long defaultStreamErrorCode, Action<long> addBytesSent)
        => new MsQuicWebTransportStream(type, quicStream, defaultStreamErrorCode, addBytesSent);

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
                _tcsWritesClosed.SetException(ExceptionHandler(ex));
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
                _tcsReadsClosed.SetException(ExceptionHandler(ex));
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
        await _quicStream.WriteAsync(initialBytes, cancellationToken).ConfigureAwait(false);
        await _quicStream.WriteAsync(encodedSessionId, cancellationToken).ConfigureAwait(false);
    }

    public override long StreamId => _quicStream.Id;

    /// <inheritdoc/>
    public override bool CanRead => !_isDisposed && _readStream.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => !_isDisposed && _readStream.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => !_isDisposed && _quicStream.CanWrite;

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

        lock (_abortDisposeLock)
        {
            _quicStream.Abort(abortDirection, (long)httpErrorCode);
        }
    }

    public override void Abort(WebTransportAbortDirection abortDirection, long errorCode)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicAbortDirection quicAbortDirection = WebTransportAbortDirectionToQuicAbortDirection(abortDirection);
        long remappedErrorCode = ErrorCodeRemapping.WebTransportCodeToHttpCode(errorCode);
        try
        {
            // Doesn't need to acquire _abortLock lock, because the use should never call WebTransportStream.Abort and WebTransportStream.DisposeAsync in parallel
            _quicStream.Abort(quicAbortDirection, remappedErrorCode);
        }
        catch (QuicException quicException)
        {
            throw ExceptionHandler(quicException);
        }
    }

    public override void CompleteWrites()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _quicStream.CompleteWrites();
    }

    #region Reads

    /// <inheritdoc/>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        try
        {
            return _readStream.BeginRead(buffer, offset, count, callback, state);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override int EndRead(IAsyncResult asyncResult)
    {
        try
        {
            return _readStream.EndRead(asyncResult);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _readStream.Read(buffer, offset, count);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        try
        {
            return _readStream.ReadByte();
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return _readStream.Read(buffer);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

// The reason for disabling CA2016 is that we handle the cancellation manually in this class using RegisterCancellationCallback
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        CancellationTokenRegistration ctr = RegisterCancellationCallback(QuicAbortDirection.Read, cancellationToken);
        await using (ctr.ConfigureAwait(false))
        {
            try
            {
                return await _readStream.ReadAsync(buffer.AsMemory(offset, count)).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw ExceptionHandler(ex, cancellationToken);
            }
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        CancellationTokenRegistration ctr = RegisterCancellationCallback(QuicAbortDirection.Read, cancellationToken);
        await using (ctr.ConfigureAwait(false))
        {
            try
            {
                return await _readStream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw ExceptionHandler(ex, cancellationToken);
            }
        }
    }

#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods

    /// <inheritdoc/>
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!CanRead)
        {
            throw new InvalidOperationException(SR.net_webtransport_stream_reading_not_allowed);
        }

        // No need to setup a cancellation callback here, the ReadAsync calls inside CopyToAsync will do that.

        return base.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    /// <inheritdoc/>
    public override void CopyTo(Stream destination, int bufferSize)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!CanRead)
        {
            throw new InvalidOperationException(SR.net_webtransport_stream_reading_not_allowed);
        }

        base.CopyTo(destination, bufferSize);
    }

    #endregion

    #region Writes

    /// <inheritdoc/>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        _addBytesSent(count);

        try
        {
            return _quicStream.BeginWrite(buffer, offset, count, callback, state);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override void EndWrite(IAsyncResult asyncResult)
    {
        try
        {
            _quicStream.EndWrite(asyncResult);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        _addBytesSent(1);

        try
        {
            _quicStream.WriteByte(value);
        }
        catch (QuicException ex)
        {
            throw ExceptionHandler(ex);
        }
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _addBytesSent(buffer.Length);

        try
        {
            _quicStream.Write(buffer);
        }
        catch (QuicException quicException)
        {
            throw ExceptionHandler(quicException);
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        _addBytesSent(count);

        try
        {
            _quicStream.Write(buffer, offset, count);
        }
        catch (QuicException quicException)
        {
            throw ExceptionHandler(quicException);
        }
    }

// The reason for disabling CA2016 is that we handle the cancellation manually in this class using RegisterCancellationCallback
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods

    /// <inheritdoc/>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        _addBytesSent(count);

        CancellationTokenRegistration ctr = RegisterCancellationCallback(QuicAbortDirection.Write, cancellationToken);
        await using (ctr.ConfigureAwait(false))
        {
            try
            {
                await _quicStream.WriteAsync(buffer.AsMemory(offset, count)).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw ExceptionHandler(ex, cancellationToken);
            }
        }
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken cancellationToken = default)
    {
        _addBytesSent(buffer.Length);

        CancellationTokenRegistration ctr = RegisterCancellationCallback(QuicAbortDirection.Write, cancellationToken);
        await using (ctr.ConfigureAwait(false))
        {
            try
            {
                await _quicStream.WriteAsync(buffer, completeWrites).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw ExceptionHandler(ex, cancellationToken);
            }
        }
    }

#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods

    #endregion

    /// <inheritdoc/>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            _quicStream.Flush();
        }
        catch (QuicException quicException)
        {
            throw ExceptionHandler(quicException);
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        CancellationTokenRegistration ctr = RegisterCancellationCallback(QuicAbortDirection.Write, cancellationToken);
        await using (ctr.ConfigureAwait(false))
        {
            try
            {
// The reason for disabling CA2016 is that we handle the cancellation manually in this class using RegisterCancellationCallback
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
                await _quicStream.FlushAsync().ConfigureAwait(false);
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
            }
            catch (QuicException quicException)
            {
                throw ExceptionHandler(quicException, cancellationToken);
            }
        }
    }

    /// <remarks>Instead of passing the cancellation tokens to the QUIC stream operations we handle the cancellation request in this class. This gives us the possibility to apply the <see cref="_remappedDefaultStreamErrorCode"/>.</remarks>
    private CancellationTokenRegistration RegisterCancellationCallback(QuicAbortDirection abortDirection, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return cancellationToken.Register(() =>
            {
                _quicStream.Abort(abortDirection, _remappedDefaultStreamErrorCode);
            });
        }
        return default;
    }

    private Exception ExceptionHandler(QuicException quicException, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, quicException);

        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException();
        }

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
                // The write side is closed gracefully by QuicStream.Dispose/DisposeAsync
                lock (_abortDisposeLock)
                {
                    _quicStream.Abort(QuicAbortDirection.Read, _remappedDefaultStreamErrorCode);
                }
                _readStream.Dispose();
                _quicStream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (!_isDisposed)
        {
            // The write side is closed gracefully by QuicStream.Dispose/DisposeAsync
            lock (_abortDisposeLock)
            {
                _quicStream.Abort(QuicAbortDirection.Read, _remappedDefaultStreamErrorCode);
            }
            await _quicStream.DisposeAsync().ConfigureAwait(false);
            await _readStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
