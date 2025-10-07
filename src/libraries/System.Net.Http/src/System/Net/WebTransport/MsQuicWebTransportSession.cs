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
    private object SyncObj { get; } = new();
    private bool _isDisposed;
    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;
    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;
    private List<MsQuicWebTransportStream> _openStreams = [];
    private readonly ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;
    private readonly QuicStream _connectStream;
    private readonly IMsQuicWebTransportSessionConnectionManager _connectionManager;
    /// <summary>
    /// Used to synchronize sending of capsules using <see cref="_capsuleSender"/> and access to configuration
    /// properties for peer (<see cref="WebTransportSession.BidirectionalStreamCountLimitForPeer"/>,
    /// <see cref="WebTransportSession.UnidirectionalStreamCountLimitForPeer"/>, <see cref="WebTransportSession.DataSentLimitForPeer"/>).
    /// </summary>
    private readonly SemaphoreSlim _forPeerConfigurationSemaphore = new(1, 1);

    /// <exception cref="ArgumentNullException">When any parameter except <paramref name="subprotocol"/> and <paramref name="id"/> is null.</exception>
    internal MsQuicWebTransportSession(
        long id,
        IMsQuicWebTransportSessionConnectionManager connectionManager,
        QuicStream connectStream,
        byte[] controlStreamBuffer,
        Channel<ChannelItem> pendingUnidirectionalStreams,
        Channel<ChannelItem> pendingBidirectionalStreams,
        Func<WebTransportSession, Task> gracefulShutdownHandler,
        string? subprotocol) : base(id, gracefulShutdownHandler, subprotocol)
    {
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(connectStream);
        ArgumentNullException.ThrowIfNull(controlStreamBuffer);
        ArgumentNullException.ThrowIfNull(pendingUnidirectionalStreams);
        ArgumentNullException.ThrowIfNull(pendingBidirectionalStreams);

        _connectStream = connectStream;
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;
        _connectionManager = connectionManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, controlStreamBuffer, this);
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
        get
        {
            lock (SyncObj) { return field; }
        }
        protected set
        {
            Debug.Assert(Monitor.IsEntered(SyncObj));

            field = value;
        }
    }

    public override long? CloseStatusCode
    {
        get
        {
            lock (SyncObj) { return field; }
        }
        protected set
        {
            Debug.Assert(Monitor.IsEntered(SyncObj));

            field = value;
        }
    }

    public override WebTransportSessionState State
    {
        get
        {
            lock (SyncObj) { return field; }
        }
        protected set
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"State transition from {field} to {value}");

            Debug.Assert(Monitor.IsEntered(SyncObj));

            field = value;
        }
    }

    internal void Init()
    {
        lock (SyncObj)
        {
            State = WebTransportSessionState.Open;
        }

        _ = ReactToWritesClosedOnConnectStream();
        _ = ProcessIncomingCapsules();
    }

    private async Task ReactToWritesClosedOnConnectStream()
    {
        try
        {
            await _connectStream.WritesClosed.ConfigureAwait(false);
        }
        catch (Exception) { }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream writes closed. Aborting read side...");

        lock (SyncObj)
        {
            if (State == WebTransportSessionState.Open)
            {
                MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedRemotely, null, null);
                CloseOpenStreamsAndCleanupPendingChannelsAndCloseConnectStream(Http3ErrorCode.WebtransportSessionGone);
            }
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

            // Clean termination of the CONNECT stream should be equivalent to status code 0 and description equal to an emtpy string
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-6-9
            ReceiveClose(0, "");
        }
        catch (Exception ex)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

            lock (SyncObj)
            {
                if (State == WebTransportSessionState.Open)
                {
                    if (ex is CapsuleProtocolException)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "Invalid capsule received on CONNECT stream. Closing session...");

                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedLocally, null, null);
                    }
                    else if (ex is QuicException qex && qex.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream closed. Closing session if not already closed...");

                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedRemotely, null, null);
                    }
                    else
                    {
                        Debug.Fail("Unexpected exception from capsule processing.");
                    }

                    CloseOpenStreamsAndCleanupPendingChannelsAndCloseConnectStream(Http3ErrorCode.WebtransportSessionGone);
                }
            }
        }

        _connectionManager.FinishedUsingConnectStream(_connectStream);
    }

    private void MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState state, long? closeStatusCode, string? closeStatusDescription)
    {
        Debug.Assert(Monitor.IsEntered(SyncObj));
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

        Exception? ex = GetExceptionForObjectState();

        Debug.Assert(ex != null);

        _pendingUnidirectionalStreams?.Writer.TryComplete(ex);
        _pendingBidirectionalStreams?.Writer.TryComplete(ex);
    }

    private async ValueTask CloseSessionBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncStarted(this);

        lock (SyncObj)
        {
            ThrowIfInvalidState();

            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
        }

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);

        // TODO: what if this fails? the session is already marked as closed
        await SendCapsuleAsync(
            closeSessionCapsule,
            static (session, capsule) => { },
            completeWrites: true,
            cancellationToken
            ).ConfigureAwait(false);

        _connectStream.Abort(QuicAbortDirection.Read, 0); // RFC suggests optional abort of the read side of the CONNECT stream after CLOSE_WEBTRANSPORT_SESSION

        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncCompleted(this);
    }

    public override async ValueTask SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

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
            CapsuleSenderExceptionHandler(ex);
            throw;
        }
        finally
        {
            _forPeerConfigurationSemaphore.Release();
        }
    }

    protected override async Task<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreStarted(this);

        MsQuicWebTransportStream wtStream;
        try
        {
            Channel<ChannelItem>? channel = type switch
            {
                WebTransportStreamType.Unidirectional => _pendingUnidirectionalStreams,
                WebTransportStreamType.Bidirectional => _pendingBidirectionalStreams,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Invalid abort direction.")
            };

            ObjectDisposedException.ThrowIf(channel == null, this);

            ChannelItem channelItem;
            try
            {
                channelItem = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                Debug.Assert(ex.InnerException != null);
                throw ex.InnerException;
            }

            Debug.Assert(type == WebTransportStreamType.Bidirectional ? channelItem.QuicStream.CanWrite : !channelItem.QuicStream.CanWrite);
            Debug.Assert(channelItem.QuicStream.CanRead);

            wtStream = MsQuicWebTransportStream.CreateInboundStream(type, channelItem.ArrayBuffer, channelItem.QuicStream);

            _ = CleanUpWebTransportStreamWhenClosed(wtStream, InboundStreamCleanup);

            AddToOpenStreamsOtherwiseDisposeStream(wtStream);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private void AddToOpenStreamsOtherwiseDisposeStream(MsQuicWebTransportStream stream)
    {
        try
        {
            lock (SyncObj)
            {
                ThrowIfInvalidState();
                _openStreams.Add(stream);
            }
        }
        catch (Exception)
        {
            stream.AbortQuicStream(QuicAbortDirection.Both, Http3ErrorCode.WebtransportSessionGone);
            stream.DisposeAsync().AsTask();
            throw;
        }
    }

    protected override async Task<WebTransportStream> OpenOutboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.OpenOutboundStreamCoreStarted(this);

        QuicStreamType quicStreamType = WebTransportStreamTypeToQuicStreamType(type);

        MsQuicWebTransportStream wtStream;
        try
        {
            try
            {
                QuicStream quicStream = await _connectionManager.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
                wtStream = MsQuicWebTransportStream.CreateOutboundStream(type, quicStream);
                await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                if (ex is QuicException qex && qex.QuicError == QuicError.TransportError)
                {
                    throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error occurred.", ex);
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

            _ = CleanUpWebTransportStreamWhenClosed(wtStream, OutboundStreamCleanup);

            AddToOpenStreamsOtherwiseDisposeStream(wtStream);
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
            _ => throw new ArgumentOutOfRangeException(paramName, streamType, "Invalid stream type.")
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

    private void OutboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        StreamCleanup(stream);
        _connectionManager.FinishedUsingOutboundStream();
    }

    private void InboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        StreamCleanup(stream);
    }

    private void StreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncObj)
        {
            _openStreams.Remove(stream);
        }
    }

    public override void Close()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncObj)
        {
            ThrowIfInvalidState();
            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
        }

        CloseOpenStreamsAndCleanUpPendingChannels(Http3ErrorCode.WebtransportSessionGone);
        CloseSessionBySendingFinOnConnectStream();
    }

    private void CloseSessionBySendingFinOnConnectStream()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncStarted(this);

        try
        {
            _connectStream.CompleteWrites();
        }
        catch (QuicException e)
        {
            throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error when closing the session.", e);
        }

        _connectStream.Abort(QuicAbortDirection.Read, 0);


        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncCompleted(this);
    }

    protected override async Task CloseAsyncCore(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        await CloseSessionBySendingCloseCapsuleAsync((uint)closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public override async Task RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

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
        lock (SyncObj)
        {
            if (State != WebTransportSessionState.Open)
            {
                return;
            }
            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedRemotely, closeStatus, statusDescription);
        }

        CloseOpenStreamsAndCleanupPendingChannelsAndCloseConnectStream(Http3ErrorCode.WebtransportSessionGone);
    }

    private void CapsuleSenderExceptionHandler(Exception ex)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

        lock (SyncObj)
        {
            ThrowIfInvalidState();
        }

        if (ex is QuicException qex)
        {
            if (qex.QuicError == QuicError.TransportError)
            {
                throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error occurred.", ex);
            }
            else if (qex.QuicError == QuicError.OperationAborted)
            {
                throw new InvalidOperationException("The session has been closed.", ex);
            }
            else if (qex.QuicError is QuicError.StreamAborted or QuicError.ConnectionAborted)
            {
                throw new WebTransportException(WebTransportError.SessionClosedByPeer, "The session was abortively closed by peer.", ex);
            }
        }
    }

    /// <summary>
    /// Call when GOAWAY or DRAIN_WEBTRANSPORT_SESSION has been received.
    /// </summary>
    /// <remarks>Does not throw.</remarks>
    internal async Task GracefulShutdown()
    {
        try
        {
            await gracefulShutdownHandler().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);

            lock (SyncObj)
            {
                if (State == WebTransportSessionState.Open)
                {
                    MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedLocally, null, null);
                }
            }

            CloseOpenStreamsAndCleanupPendingChannelsAndCloseConnectStream(Http3ErrorCode.WebtransportSessionGone);
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
            _ = GracefulShutdown();
        }
    }

    internal void CloseOpenStreamsAndCleanupPendingChannelsAndCloseConnectStream(Http3ErrorCode httpErrorCode)
    {
        CloseOpenStreamsAndCleanUpPendingChannels(httpErrorCode);

        _connectStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
    }

    private void CloseOpenStreamsAndCleanUpPendingChannels(Http3ErrorCode httpErrorCode)
    {
        CloseAndDisposePendingStreams(httpErrorCode);

        lock (SyncObj)
        {
            foreach (MsQuicWebTransportStream item in _openStreams)
            {
                item.AbortQuicStream(QuicAbortDirection.Both, httpErrorCode);
            }

            _openStreams = [];
        }
    }

    private void CloseAndDisposePendingStreams(Http3ErrorCode httpErrorCode)
    {
        CloseAndDisposeAllStreamsInChannel(_pendingUnidirectionalStreams, httpErrorCode);
        CloseAndDisposeAllStreamsInChannel(_pendingBidirectionalStreams, httpErrorCode);
    }

    private static void CloseAndDisposeAllStreamsInChannel(Channel<ChannelItem>? channel, Http3ErrorCode httpErrorCode)
    {
        Debug.Assert(!(channel?.Writer.TryComplete() ?? false));

        while (channel?.Reader.TryRead(out ChannelItem item) ?? false)
        {
            item.ArrayBuffer.Dispose();
            item.QuicStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
            item.QuicStream.Dispose();
        }
    }

    protected override async ValueTask DisposeAsyncCore(bool disposing)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"{nameof(_isDisposed)}={_isDisposed}");

        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                List<MsQuicWebTransportStream> openStreams;
                lock (SyncObj)
                {
                    if (State == WebTransportSessionState.Open)
                    {
                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
                    }
                    openStreams = _openStreams;
                    _openStreams = [];
                }

                _connectStream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);

                List<Task> closeOpenWtStreamsTasks = new();
                foreach (WebTransportStream wtStream in openStreams)
                {
                    closeOpenWtStreamsTasks.Add(wtStream.DisposeAsync().AsTask());
                }
                await Task.WhenAll(closeOpenWtStreamsTasks).ConfigureAwait(false); // these tasks should always succeed - DisposeAsync never throws

                CloseAndDisposeAllStreamsInChannel(_pendingUnidirectionalStreams, Http3ErrorCode.WebtransportSessionGone);
                CloseAndDisposeAllStreamsInChannel(_pendingBidirectionalStreams, Http3ErrorCode.WebtransportSessionGone);

                _pendingUnidirectionalStreams = null;
                _pendingBidirectionalStreams = null;

                _capsuleConsumer.Dispose();

                _connectionManager.FinishedUsingConnectStream(_connectStream);
            }
        }

        await base.DisposeAsyncCore(disposing).ConfigureAwait(false);
    }
}
