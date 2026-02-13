// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Net.Http;
using System.Net.Quic;
using System.Threading.Channels;
using System.Diagnostics;
using ChannelItem = (System.Net.ArrayBuffer ArrayBuffer, System.Net.Quic.QuicStream QuicStream);
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Net.WebTransport;


/// <summary>
/// Implementation of a WebTransport session that uses <see cref="Quic"/>.
/// </summary>
internal sealed class MsQuicWebTransportSession : WebTransportSession
{
    /// <summary>
    /// Lock this object when working with <see cref="WebTransportSession.State"/>, <see cref="WebTransportSession.CloseStatusCode"/>,
    /// ,<see cref="WebTransportSession.CloseStatusDescription"/> or <see cref="_openStreams"/>.
    /// </summary>
    private Lock SyncLock { get; } = new();

    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;

    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;

    private readonly List<MsQuicWebTransportStream> _openStreams = [];

    private readonly ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;
    private readonly QuicStream _connectStream;
    private readonly IMsQuicWebTransportSessionConnectionManager _connectionManager;
    /// <summary>
    /// Used to make <see cref="DisposeAsyncCore"/> thread-safe.
    /// </summary>
    /// <remarks>
    /// We use a semaphore instead of compare-and-exchange operations to make sure, that when call of <see cref="DisposeAsyncCore"/> finishes, all resources have been released.
    /// We use a semaphore instead of a standard lock to, because we need to use await in the clean up.
    /// </remarks>
    private readonly SemaphoreSlim _cleanUpSemaphore = new(1, 1);
    private bool _isCleanedUp;

    /// <summary>
    /// Used to synchronize sending of capsules using <see cref="_capsuleSender"/> and access to configuration
    /// properties for peer (<see cref="WebTransportSession.BidirectionalStreamCountLimitForPeer"/>,
    /// <see cref="WebTransportSession.UnidirectionalStreamCountLimitForPeer"/>, <see cref="WebTransportSession.DataSentLimitForPeer"/>).
    /// </summary>
    private readonly SemaphoreSlim _forPeerConfigurationSemaphore = new(1, 1);

    private long _bytesSent;
    private Lock BytesSentLock { get; } = new();

    private readonly SemaphoreSlim _unidirectionalStreamSemaphore = new(0);
    private readonly SemaphoreSlim _bidirectionalStreamSemaphore = new(0);

    /// <exception cref="ArgumentNullException">When any parameter except <paramref name="subprotocol"/> and <paramref name="id"/> is null.</exception>
    internal MsQuicWebTransportSession(
        long id,
        IMsQuicWebTransportSessionConnectionManager connectionManager,
        QuicStream connectStream,
        ArrayBuffer connectStreamBuffer,
        Channel<ChannelItem> pendingUnidirectionalStreams,
        Channel<ChannelItem> pendingBidirectionalStreams,
        Func<WebTransportSession, Task> gracefulShutdownHandler,
        string? subprotocol,
        long defaultStreamErrorCode) : base(id, gracefulShutdownHandler, subprotocol, defaultStreamErrorCode)
    {
        _connectStream = connectStream;
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;
        _connectionManager = connectionManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, connectStreamBuffer, this);
        _capsuleSender = new CapsuleSender(connectStream);


        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.Associate(this, _connectStream);
            NetEventSource.Associate(this, _connectionManager);
            NetEventSource.Associate(this, _capsuleConsumer);
            NetEventSource.Associate(this, _capsuleSender);
        }

        byte[] buffer = new byte[VariableLengthIntegerHelper.MaximumEncodedLength];
        VariableLengthIntegerHelper.TryWrite(buffer, id, out int bytesWritten);
        _idEncodedAsVariableLengthInteger = buffer.AsMemory().Slice(0, bytesWritten);
    }

    public override string? CloseStatusDescription
    {
        // Lock not required for getter because this property is only read after the thread reading it has observed the session as closed, which requires acquiring the SyncLock, which ensures memory synchronization.
        get;
        protected set
        {
            Debug.Assert(SyncLock.IsHeldByCurrentThread);

            field = value;
        }
    }

    public override long? CloseStatusCode
    {
        // Lock not required for getter because this property is only read after the thread reading it has observed the session as closed, which requires acquiring the SyncLock, which ensures memory synchronization.
        get;
        protected set
        {
            Debug.Assert(SyncLock.IsHeldByCurrentThread);

            field = value;
        }
    }

    public override WebTransportSessionState State
    {
        get
        {
            lock (SyncLock) { return field; }
        }
        protected set
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"State transition from {field} to {value}");

            Debug.Assert(SyncLock.IsHeldByCurrentThread);

            field = value;
        }
    }

    #region Session configuration

    /// <remarks>This field can be read during a write operation (always reads a valid value). This field can not be written to by two threads at the same time.</remarks>
    public override long UnidirectionalStreamCountLimitProvidedByPeer
    {
        get => Interlocked.Read(ref field);
        internal set
        {
            ThrowHelper.ValidateStreamCountLimit(value);

            int newValue = (int)value;
            int increase = newValue - (int)Interlocked.Read(ref field);
            if (increase > 0)
            {
                _unidirectionalStreamSemaphore.Release(increase);
            }

            Interlocked.Exchange(ref field, newValue);
        }
    }

    public override long UnidirectionalStreamCountLimitForPeer
    {
        get => Interlocked.Read(ref field);
        protected set => Interlocked.Exchange(ref field, value);
    }

    /// <remarks>This field can be read during a write operation (always reads a valid value). This field can not be written to by two threads at the same time.</remarks>
    public override long BidirectionalStreamCountLimitProvidedByPeer
    {
        get => Interlocked.Read(ref field);
        internal set
        {
            ThrowHelper.ValidateStreamCountLimit(value);

            int newValue = (int)value;
            int increase = newValue - (int)Interlocked.Read(ref field);
            if (increase > 0)
            {
                _bidirectionalStreamSemaphore.Release(increase);
            }

            Interlocked.Exchange(ref field, newValue);
        }
    }

    public override long BidirectionalStreamCountLimitForPeer
    {
        get => Interlocked.Read(ref field);
        protected set => Interlocked.Exchange(ref field, value);
    }

    public override long DataSentLimitProvidedByPeer
    {
        get => Interlocked.Read(ref field);
        internal set => Interlocked.Exchange(ref field, value);
    }

    public override long DataSentLimitForPeer
    {
        get => Interlocked.Read(ref field);
        protected set => Interlocked.Exchange(ref field, value);
    }

    #endregion

    internal void Init()
    {
        lock (SyncLock)
        {
            State = WebTransportSessionState.Open;
        }

        _ = ReactToWritesClosedAbortivelyOnConnectStream();
        _ = ProcessIncomingCapsules();
    }

    private async Task ReactToWritesClosedAbortivelyOnConnectStream()
    {
        try
        {
            await _connectStream.WritesClosed.ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (SyncLock)
            {
                if (State == WebTransportSessionState.Open)
                {
                    if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream writes closed while session is open. Closing session...");

                    MarkSessionAsClosed(WebTransportSessionState.AbortedRemotely, null, null);
                }
            }

            await CleanUpSessionAsync(Http3ErrorCode.WebtransportSessionGone).ConfigureAwait(false);
        }
    }

    private async Task ProcessIncomingCapsules()
    {
        try
        {
            while (true)
            {
                await _capsuleConsumer.ProcessNextCapsule().ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException ex) // Clean termination
        {
            if (NetEventSource.Log.IsEnabled())
            {
                NetEventSource.TraceException(this, ex);
                NetEventSource.Trace(this, "CONNECT stream closed cleanly by peer. Closing session...");
            }

            // Clean termination of the CONNECT stream should be equivalent to status code 0 and description equal to an empty string
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-6-9
            ReceiveClose(0, "");
        }
        catch (Exception ex)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

            lock (SyncLock)
            {
                if (State == WebTransportSessionState.Open)
                {
                    // TODO: store the exception and rethrow it
                    if (ex is CapsuleProtocolException)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "Invalid or unsupported configuration received on CONNECT stream. Aborting session...");

                        MarkSessionAsClosed(WebTransportSessionState.AbortedLocally, null, null);
                    }
                    else if (ex is QuicException qex && qex.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream aborted. Aborting session if not already closed...");

                        MarkSessionAsClosed(WebTransportSessionState.AbortedRemotely, null, null);
                    }
                    else
                    {
                        Debug.Fail("Unexpected exception from capsule processing.");
                    }

                }
            }
        }

        await CleanUpSessionAsync(Http3ErrorCode.WebtransportSessionGone).ConfigureAwait(false);
    }

    private void AddBytesSent(long bytes)
    {
        Debug.Assert(bytes >= 0);

        lock (BytesSentLock)
        {
            if (_bytesSent + bytes > DataSentLimitProvidedByPeer)
            {
                throw new WebTransportException(WebTransportError.LimitExceeded, SR.net_webtransport_data_limit_exceeded);
            }
            _bytesSent += bytes;
        }
    }

    private void MarkSessionAsClosed(WebTransportSessionState state, long? closeStatusCode, string? closeStatusDescription)
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread);
        Debug.Assert(State == WebTransportSessionState.Open);
        Debug.Assert(
            state
                is WebTransportSessionState.ClosedRemotely
                or WebTransportSessionState.ClosedLocally
                or WebTransportSessionState.AbortedLocally
                or WebTransportSessionState.AbortedRemotely
                );

        State = state;
        CloseStatusCode = closeStatusCode;
        CloseStatusDescription = closeStatusDescription;
    }

    private async ValueTask CloseSessionBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncStarted(this);

        lock (SyncLock)
        {
            if (State != WebTransportSessionState.Open)
            {
                return;
            }

            MarkSessionAsClosed(WebTransportSessionState.ClosedLocally, null, null);
        }

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);

        try
        {
            await SendCapsuleAsync(
                closeSessionCapsule,
                static (session, capsule) => { },
                completeWrites: true,
                cancellationToken
                ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);
            // RFC: The delivery of the error code and string MAY be best-effort.
            // Therefore, we just close the session without the delivery of the error code and description in the finally block;
            throw;
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }


        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncCompleted(this);
    }

    public override async ValueTask SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowHelper.ValidateStreamCountLimit(limit);

        ThrowIfInvalidState();

        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxUnidirectionalStreamsCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            static (session, capsule) => session.UnidirectionalStreamCountLimitForPeer = capsule.MaxUnidirectionalStreams,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    public override async ValueTask SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowHelper.ValidateStreamCountLimit(limit);

        ThrowIfInvalidState();
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxBidirectionalStreamsCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            stateUpdate: static (session, capsule) => session.BidirectionalStreamCountLimitForPeer = capsule.MaxBidirectionalStreams,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    public override async ValueTask SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);

        MaxDataCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            static (session, capsule) => session.DataSentLimitForPeer = capsule.MaxData,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    private async Task SendCapsuleAsync<TCapsule>(TCapsule capsule, Action<MsQuicWebTransportSession, TCapsule> stateUpdate, bool completeWrites, CancellationToken cancellationToken) where TCapsule : Capsule
    {
        await _forPeerConfigurationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _capsuleSender.SendCapsuleAsync(capsule, completeWrites, cancellationToken).ConfigureAwait(false);

            stateUpdate(this, capsule);
        }
        catch (Exception ex)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);
            CapsuleSenderExceptionHandler(ex);
            throw;
        }
        finally
        {
            _forPeerConfigurationSemaphore.Release();
        }
    }

    protected override async ValueTask<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreStarted(this);

        MsQuicWebTransportStream wtStream;
        try
        {
            Channel<ChannelItem>? channel = type switch
            {
                WebTransportStreamType.Unidirectional => _pendingUnidirectionalStreams,
                WebTransportStreamType.Bidirectional => _pendingBidirectionalStreams,
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };

            ObjectDisposedException.ThrowIf(channel == null, this);

            ChannelItem channelItem;
            try
            {
                channelItem = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);
                Debug.Assert(ex.InnerException != null);
                throw ex.InnerException;
            }

            Debug.Assert(type == WebTransportStreamType.Bidirectional ? channelItem.QuicStream.CanWrite : !channelItem.QuicStream.CanWrite);
            Debug.Assert(channelItem.QuicStream.CanRead);

            wtStream = MsQuicWebTransportStream.CreateInboundStream(type, channelItem.ArrayBuffer, channelItem.QuicStream, DefaultStreamErrorCode, AddBytesSent);

            _ = CleanUpWebTransportStreamWhenClosed(wtStream, InboundStreamCleanup);

            AddToOpenStreamsOtherwiseRejectAndDisposeStream(wtStream);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private void AddToOpenStreamsOtherwiseRejectAndDisposeStream(MsQuicWebTransportStream stream)
    {
        lock (SyncLock)
        {
            Exception? e = GetExceptionForObjectState();

            if (e is null)
            {
                _openStreams.Add(stream);
            }
            else
            {
                RejectAndDisposeStream(stream);
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);
                throw e;
            }
        }

        static void RejectAndDisposeStream(MsQuicWebTransportStream streamToReject)
        {
            streamToReject.AbortQuicStream(QuicAbortDirection.Both, Http3ErrorCode.WebtransportSessionGone);
            streamToReject.DisposeAsync().AsTask();
        }
    }

    protected override async ValueTask<WebTransportStream> OpenOutboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.OpenOutboundStreamCoreStarted(this);

        SemaphoreSlim streamSemaphore = type switch
        {
            WebTransportStreamType.Unidirectional => _unidirectionalStreamSemaphore,
            WebTransportStreamType.Bidirectional => _bidirectionalStreamSemaphore,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        await streamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        QuicStreamType quicStreamType = WebTransportStreamTypeToQuicStreamType(type);

        MsQuicWebTransportStream? wtStream = null;
        try
        {
            QuicStream? quicStream = null;
            try
            {
                try
                {
                    quicStream = await _connectionManager.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    streamSemaphore.Release();
                    throw;
                }
                wtStream = MsQuicWebTransportStream.CreateOutboundStream(type, quicStream, DefaultStreamErrorCode, AddBytesSent);
                await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

                if (wtStream != null)
                {
                    OpenOutboundStreamCleanup(wtStream);
                    await wtStream.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    Debug.Assert(quicStream == null, "It should be impossible for the quic stream to be created and the WebTransport stream to be null.");
                }

                if (ex is QuicException qex && qex.QuicError == QuicError.TransportError)
                {
                    throw new WebTransportException(WebTransportError.TransportLayerError, SR.net_webtransport_transport_layer_error, ex);
                }
                else if (ex is OperationCanceledException or InvalidOperationException)
                {
                    throw ex;
                }
                else
                {
                    ThrowIfInvalidState();
                }

                throw;
            }

            _ = CleanUpWebTransportStreamWhenClosed(wtStream, OpenOutboundStreamCleanup);

            AddToOpenStreamsOtherwiseRejectAndDisposeStream(wtStream);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.OpenOutboundStreamCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private static QuicStreamType WebTransportStreamTypeToQuicStreamType(WebTransportStreamType streamType, [CallerArgumentExpression(nameof(streamType))] string? paramName = null)
    {
        return streamType switch
        {
            WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
            WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(paramName, streamType, SR.Format(SR.net_webtransport_invalid_stream_type, streamType))
        };
    }

    private static Task CleanUpWebTransportStreamWhenClosed(MsQuicWebTransportStream stream, Action<MsQuicWebTransportStream> cleanUpAction)
    {
        return Task.Run(async () =>
        {
            try
            {
                await stream.ReadsClosed.ConfigureAwait(false);
            }
            catch (Exception) { }

            try
            {
                await stream.WritesClosed.ConfigureAwait(false);
            }
            catch (Exception) { }

            cleanUpAction(stream);
        });
    }

    private void OpenOutboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        StreamCleanup(stream);
        _connectionManager.RemoveOutboundStream();
    }

    private void InboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        StreamCleanup(stream);
    }

    private void StreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncLock)
        {
            _openStreams.Remove(stream);
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncLock)
        {
            if (State == WebTransportSessionState.Open)
            {
                MarkSessionAsClosed(WebTransportSessionState.ClosedLocally, null, null);
            }
        }

        // Graceful close (sending FIN on the CONNECT stream) is performed inside CleanUpSessionAsync
        // rather than here to avoid a race condition. Background tasks (ProcessIncomingCapsules,
        // ReactToWritesClosedAbortivelyOnConnectStream) may call CleanUpSessionAsync concurrently,
        // which could dispose _connectStream while we're still trying to use it here.
        // By moving the graceful close inside the semaphore-protected cleanup, we ensure all
        // _connectStream operations are properly serialized.
        await CleanUpSessionAsync(Http3ErrorCode.WebtransportSessionGone).ConfigureAwait(false);
    }

    protected override async ValueTask CloseAsyncCore(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        await CloseSessionBySendingCloseCapsuleAsync((uint)closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncLock)
        {
            if (State != WebTransportSessionState.Open)
            {
                return;
            }
        }

        await RequestCloseAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    private async Task RequestCloseAsyncCore(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.RequestCloseAsyncCoreStarted(this);

        try
        {
            await SendCapsuleAsync(
                DrainSessionCapsule.Instance,
                static (session, capsule) => { },
                completeWrites: false,
                cancellationToken
                ).ConfigureAwait(false);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.RequestCloseAsyncCoreCompleted(this);
        }
    }


    internal override void ReceiveClose(uint closeStatus, string statusDescription)
    {
        lock (SyncLock)
        {
            if (State != WebTransportSessionState.Open)
            {
                return;
            }
            MarkSessionAsClosed(WebTransportSessionState.ClosedRemotely, closeStatus, statusDescription);
        }

        _ = CleanUpSessionAsync(Http3ErrorCode.WebtransportSessionGone).AsTask();
    }

    private void CapsuleSenderExceptionHandler(Exception ex)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

        lock (SyncLock)
        {
            ThrowIfInvalidState();
        }

        if (ex is QuicException qex)
        {
            if (qex.QuicError == QuicError.TransportError)
            {
                throw new WebTransportException(WebTransportError.TransportLayerError, SR.net_webtransport_transport_layer_error, ex);
            }
            else if (qex.QuicError == QuicError.OperationAborted)
            {
                throw new WebTransportException(WebTransportError.OperationAborted, SR.net_webtransport_operation_aborted, ex);
            }
            else if (qex.QuicError is QuicError.StreamAborted or QuicError.ConnectionAborted)
            {
                throw new WebTransportException(WebTransportError.SessionClosedByPeer, SR.net_webtransport_session_closed_by_peer, ex);
            }
        }
    }

    /// <summary>
    /// Call when GOAWAY or DRAIN_WEBTRANSPORT_SESSION has been received.
    /// </summary>
    /// <remarks>Does not throw.</remarks>
    internal async Task GracefulShutdownAsync()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        try
        {
            await GracefulShutdownHandler().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);

            lock (SyncLock)
            {
                if (State == WebTransportSessionState.Open)
                {
                    MarkSessionAsClosed(WebTransportSessionState.AbortedLocally, null, null);
                }
            }

            await CleanUpSessionAsync(Http3ErrorCode.WebtransportSessionGone).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// This method should be called when peer initiates session drain operation.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.6.1"/>
    internal void ReceiveDrain()
    {
        if (State == WebTransportSessionState.Open)
        {
            _ = GracefulShutdownAsync();
        }
    }

    /// <summary>
    /// Method for cleaning up the session after it the closing handshake has been performed and the session has been marked as closed.
    /// </summary>
    /// <remarks>May be called multiple times.</remarks>
    /// <param name="errorCodeForStreams">Error code used to abort all <see cref="QuicStream"/> instances associated with this session.</param>
    private async ValueTask CleanUpSessionAsync(Http3ErrorCode errorCodeForStreams)
    {
        await _cleanUpSemaphore.WaitAsync().ConfigureAwait(false);

        if (!_isCleanedUp)
        {
            bool tryClosingGracefully;
            lock (SyncLock)
            {
                tryClosingGracefully = State == WebTransportSessionState.ClosedLocally;
            }

            await CleanUpPendingAndOpenStreamsAndCloseConnectStreamAsync(Http3ErrorCode.WebtransportSessionGone, tryClosingGracefully).ConfigureAwait(false);
            _connectionManager.RemoveSession(_connectStream); // _connectStream will be disposed by the _connectionManager
            _unidirectionalStreamSemaphore.Dispose();
            _bidirectionalStreamSemaphore.Dispose();

            _isCleanedUp = true;
        }

        _cleanUpSemaphore.Release();

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private async ValueTask CleanUpPendingAndOpenStreamsAndCloseConnectStreamAsync(Http3ErrorCode httpErrorCode, bool tryClosingGracefully)
    {
        if (tryClosingGracefully)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncStarted(this);

            try
            {
                _connectStream.CompleteWrites();
            }
            catch (Exception ex)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

                _connectStream.Abort(QuicAbortDirection.Write, 0);
            }

            _connectStream.Abort(QuicAbortDirection.Read, 0);

            if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncCompleted(this);
        }
        else
        {
            _connectStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
        }

        await CleanupPendingAndOpenStreamsAsync(httpErrorCode).ConfigureAwait(false);
    }

    private async ValueTask CleanupPendingAndOpenStreamsAsync(Http3ErrorCode httpErrorCode)
    {
        CompletePendingStreamsChannels();

        ValueTask pendingStreamTask = CloseAndCleanupPendingStreamsAsync(httpErrorCode);

        CloseOpenStreams(httpErrorCode);

        await pendingStreamTask.ConfigureAwait(false);
    }

    private void CompletePendingStreamsChannels()
    {
        WebTransportException? ex = GetExceptionForObjectState();

        Debug.Assert(ex != null);

        _pendingUnidirectionalStreams?.Writer.TryComplete(ex);
        _pendingBidirectionalStreams?.Writer.TryComplete(ex);
    }

    private void CloseOpenStreams(Http3ErrorCode httpErrorCode)
    {
        lock (SyncLock)
        {
            foreach (MsQuicWebTransportStream item in _openStreams)
            {
                item.AbortQuicStream(QuicAbortDirection.Both, httpErrorCode);
            }
        }
    }

    private async ValueTask CloseAndCleanupPendingStreamsAsync(Http3ErrorCode httpErrorCodeForPendingStreams)
    {
        ValueTask uniStreamsTask = CloseAndDisposeAllStreamsInChannel(_pendingUnidirectionalStreams, httpErrorCodeForPendingStreams);
        ValueTask biStreamsTask = CloseAndDisposeAllStreamsInChannel(_pendingBidirectionalStreams, httpErrorCodeForPendingStreams);

        await uniStreamsTask.ConfigureAwait(false);
        await biStreamsTask.ConfigureAwait(false);

        _pendingUnidirectionalStreams = null;
        _pendingBidirectionalStreams = null;

        static async ValueTask CloseAndDisposeAllStreamsInChannel(Channel<ChannelItem>? channel, Http3ErrorCode httpErrorCode)
        {
            if (channel is null) return;

            while (channel.Reader.TryRead(out ChannelItem item))
            {
                item.ArrayBuffer.Dispose();
                item.QuicStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
                await item.QuicStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
