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
    /// Lock this object when writing <see cref="_lifecycle"/> (the snapshot behind <see cref="WebTransportSession.State"/>,
    /// <see cref="WebTransportSession.CloseStatusCode"/> and <see cref="WebTransportSession.CloseStatusDescription"/>) or when working
    /// with <see cref="_openStreams"/>, <see cref="_pendingOutboundOpenWaitersCount"/>,
    /// <see cref="_hasOutboundOpenShutdownStarted"/>, <see cref="_outboundOpenWaitersDrainedTcs"/>, or <see cref="_cleanUpTask"/>.
    /// Reading the lifecycle properties does not require the lock, see <see cref="_lifecycle"/>.
    /// </summary>
    private Lock SyncLock { get; } = new();

    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;

    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;

    private readonly HashSet<MsQuicWebTransportStream> _openStreams = [];

    private readonly ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;
    private readonly QuicStream _connectStream;
    private readonly MsQuicWebTransportSessionManager _sessionManager;
    private volatile Task? _cleanUpTask;

    /// <summary>
    /// Used to synchronize sending of capsules using <see cref="_capsuleSender"/> and access to configuration
    /// properties for peer (<see cref="WebTransportSession.BidirectionalStreamCountLimitForPeer"/>,
    /// <see cref="WebTransportSession.UnidirectionalStreamCountLimitForPeer"/>, <see cref="WebTransportSession.DataSentLimitForPeer"/>).
    /// </summary>
    private readonly SemaphoreSlim _forPeerConfigurationSemaphore = new(1, 1);

    private long _bytesSent;
    private Lock BytesSentLock { get; } = new();

    /// <summary>
    /// Used to respect <see cref="WebTransportSession.UnidirectionalStreamCountLimitProvidedByPeer"/>.
    /// </summary>
    private readonly SemaphoreSlim _outboundUnidirectionalStreamSemaphore = new(0);
    /// <summary>
    /// Used to respect <see cref="WebTransportSession.BidirectionalStreamCountLimitProvidedByPeer"/>.
    /// </summary>
    private readonly SemaphoreSlim _outboundBidirectionalStreamSemaphore = new(0);

    // The following fields are used to synchronize the shutdown of all waiters of _outboundUnidirectionalStreamSemaphore and _outboundBidirectionalStreamSemaphore
    private readonly CancellationTokenSource _outboundOpenShutdownCts = new();
    private readonly TaskCompletionSource _outboundOpenWaitersDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingOutboundOpenWaitersCount;
    private bool _hasOutboundOpenShutdownStarted;

    /// <exception cref="ArgumentNullException">When any parameter except <paramref name="subprotocol"/> and <paramref name="id"/> is null.</exception>
    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportSessionManager sessionManager,
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
        _sessionManager = sessionManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, connectStreamBuffer, this);
        _capsuleSender = new CapsuleSender(connectStream);

        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.Associate(this, _connectStream);
            NetEventSource.Associate(this, _sessionManager);
            NetEventSource.Associate(this, _capsuleConsumer);
            NetEventSource.Associate(this, _capsuleSender);
        }

        byte[] buffer = new byte[VariableLengthIntegerHelper.MaximumEncodedLength];
        VariableLengthIntegerHelper.TryWrite(buffer, id, out int bytesWritten);
        _idEncodedAsVariableLengthInteger = buffer.AsMemory().Slice(0, bytesWritten);
    }

    /// <summary>
    /// Immutable snapshot of the lifecycle properties (<see cref="State"/>, <see cref="CloseStatusCode"/>
    /// and <see cref="CloseStatusDescription"/>).
    /// </summary>
    private sealed class LifecycleSnapshot
    {
        internal static readonly LifecycleSnapshot Initial = new(WebTransportSessionState.None, null, null);

        internal LifecycleSnapshot(WebTransportSessionState state, long? closeStatusCode, string? closeStatusDescription)
        {
            State = state;
            CloseStatusCode = closeStatusCode;
            CloseStatusDescription = closeStatusDescription;
        }

        internal WebTransportSessionState State { get; }
        internal long? CloseStatusCode { get; }
        internal string? CloseStatusDescription { get; }
    }

    /// <summary>
    /// The current lifecycle snapshot. Written only while holding <see cref="SyncLock"/>. Reads do not require the lock.
    /// Publishing a whole snapshot with a single reference write keeps the three values coherent for lock-free readers:
    /// the write is atomic and acts as a release with respect to the constructor's writes, the reads through the reference
    /// are data-dependent and thus ordered, and cache coherency guarantees that a reader which has observed a snapshot
    /// can never observe an older one afterwards (see docs/design/specs/Memory-model.md). Reads may return a stale
    /// snapshot because the lifecycle properties are best-effort observations rather than a notification mechanism.
    /// Callers must not poll them to detect a transition.
    /// </summary>
    private LifecycleSnapshot _lifecycle = LifecycleSnapshot.Initial;

    public override string? CloseStatusDescription => _lifecycle.CloseStatusDescription;

    public override long? CloseStatusCode => _lifecycle.CloseStatusCode;

    public override WebTransportSessionState State => _lifecycle.State;

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
                _outboundUnidirectionalStreamSemaphore.Release(increase);
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
                _outboundBidirectionalStreamSemaphore.Release(increase);
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
            Debug.Assert(State == WebTransportSessionState.None);

            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"State transition from {_lifecycle.State} to {WebTransportSessionState.Open}");

            _lifecycle = new LifecycleSnapshot(WebTransportSessionState.Open, null, null);
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
        catch (EndOfStreamException) // Clean termination
        {
            if (NetEventSource.Log.IsEnabled())
            {
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
                    if (ex is QuicException qex && qex.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream aborted. Aborting session if not already closed...");

                        MarkSessionAsClosed(WebTransportSessionState.AbortedRemotely, null, null);
                    }
                    else
                    {
                        if (ex is CapsuleProtocolException)
                        {
                            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "Invalid or unsupported configuration received on CONNECT stream. Aborting session...");
                        }
                        else
                        {
                            Debug.Fail("Unexpected exception from capsule processing.");
                        }

                        MarkSessionAsClosed(WebTransportSessionState.AbortedLocally, null, null);
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

    private void RemoveBytesSent(long bytes)
    {
        Debug.Assert(bytes >= 0);

        lock (BytesSentLock)
        {
            _bytesSent -= bytes;
            Debug.Assert(_bytesSent >= 0);
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

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"State transition from {_lifecycle.State} to {state}");

        // Publish all three values as a single snapshot so a lock-free reader cannot observe
        // the closed state without the matching status.
        _lifecycle = new LifecycleSnapshot(state, closeStatusCode, closeStatusDescription);
    }

    private void RegisterOutboundOpenWaiter()
    {
        lock (SyncLock)
        {
            ThrowIfInvalidState();
            Debug.Assert(!_hasOutboundOpenShutdownStarted, "Outbound-open shutdown should only start after the session becomes invalid.");
            _pendingOutboundOpenWaitersCount++;
        }
    }

    private void UnregisterOutboundOpenWaiter()
    {
        bool waitersDrained;

        lock (SyncLock)
        {
            Debug.Assert(_pendingOutboundOpenWaitersCount > 0);
            _pendingOutboundOpenWaitersCount--;
            waitersDrained = _hasOutboundOpenShutdownStarted && _pendingOutboundOpenWaitersCount == 0;
        }

        if (waitersDrained)
        {
            _outboundOpenWaitersDrainedTcs.TrySetResult();
        }
    }

    private async Task CleanUpOpenOutboundWaitersAsync()
    {
        bool waitersAlreadyDrained;

        lock (SyncLock)
        {
            _hasOutboundOpenShutdownStarted = true;
            waitersAlreadyDrained = _pendingOutboundOpenWaitersCount == 0;
        }

        await _outboundOpenShutdownCts.CancelAsync().ConfigureAwait(false);

        if (waitersAlreadyDrained)
        {
            _outboundOpenWaitersDrainedTcs.TrySetResult();
        }

        await _outboundOpenWaitersDrainedTcs.Task.ConfigureAwait(false);
    }

    private void ReleaseStreamSemaphoreIfOutboundOpenShutdownHasNotStarted(SemaphoreSlim streamSemaphore)
    {
        lock (SyncLock)
        {
            if (!_hasOutboundOpenShutdownStarted)
            {
                streamSemaphore.Release();
            }
        }
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

        ThrowHelper.ValidateStreamCountLimit(limit, UnidirectionalStreamCountLimitForPeer);

        ThrowIfInvalidState();

        if (limit == UnidirectionalStreamCountLimitForPeer)
        {
            return;
        }

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

        ThrowHelper.ValidateStreamCountLimit(limit, BidirectionalStreamCountLimitForPeer);

        ThrowIfInvalidState();

        if (limit == BidirectionalStreamCountLimitForPeer)
        {
            return;
        }

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

        ThrowHelper.ValidateDataLimit(limit, DataSentLimitForPeer);

        ThrowIfInvalidState();

        if (limit == DataSentLimitForPeer)
        {
            return;
        }

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

            wtStream = MsQuicWebTransportStream.CreateInboundStream(type, channelItem.ArrayBuffer, channelItem.QuicStream, DefaultStreamErrorCode, AddBytesSent, RemoveBytesSent, InboundStreamCleanup);
            AddToOpenStreamsOtherwiseRejectAndDisposeStream(wtStream, InboundStreamCleanup);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private void AddToOpenStreamsOtherwiseRejectAndDisposeStream(MsQuicWebTransportStream stream, Action<MsQuicWebTransportStream> cleanupActionBeforeDispose)
    {
        Exception? invalidStateException;

        lock (SyncLock)
        {
            invalidStateException = GetExceptionForObjectState();

            if (invalidStateException is null)
            {
                _openStreams.Add(stream);
                return;
            }
        }

        cleanupActionBeforeDispose?.Invoke(stream);
        RejectAndDisposeStream(stream);

        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, invalidStateException);
        throw invalidStateException;

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
            WebTransportStreamType.Unidirectional => _outboundUnidirectionalStreamSemaphore,
            WebTransportStreamType.Bidirectional => _outboundBidirectionalStreamSemaphore,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        RegisterOutboundOpenWaiter();

        try
        {
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outboundOpenShutdownCts.Token);

            try
            {
                await streamSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ThrowIfInvalidState();
                throw;
            }
        }
        finally
        {
            UnregisterOutboundOpenWaiter();
        }

        ThrowIfInvalidState();

        QuicStreamType quicStreamType = WebTransportStreamTypeToQuicStreamType(type);

        MsQuicWebTransportStream? wtStream = null;
        try
        {
            QuicStream? quicStream = null;
            try
            {
                try
                {
                    quicStream = await _sessionManager.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    ReleaseStreamSemaphoreIfOutboundOpenShutdownHasNotStarted(streamSemaphore);
                    throw;
                }
                wtStream = MsQuicWebTransportStream.CreateOutboundStream(type, quicStream, DefaultStreamErrorCode, AddBytesSent, RemoveBytesSent, OpenOutboundStreamCleanup);
                await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger, cancellationToken).ConfigureAwait(false);
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
                    throw;
                }
                else
                {
                    ThrowIfInvalidState();
                }

                throw;
            }

            AddToOpenStreamsOtherwiseRejectAndDisposeStream(wtStream, OpenOutboundStreamCleanup);
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

    private void OpenOutboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        bool isFirstCleanUpCall = StreamCleanup(stream);
        if (isFirstCleanUpCall)
        {
            _sessionManager.RemoveOutboundStream(WebTransportStreamTypeToQuicStreamType(stream.Type));
        }
    }

    private void InboundStreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        StreamCleanup(stream);
    }

    private bool StreamCleanup(MsQuicWebTransportStream stream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncLock)
        {
            return _openStreams.Remove(stream);
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
        // By deferring to the single cleanup task, we ensure all _connectStream operations are
        // properly serialized.
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
                throw new WebTransportException(WebTransportError.SessionAbortedByPeer, SR.net_webtransport_session_aborted_by_peer, ex);
            }
        }

        throw new WebTransportException(WebTransportError.InternalError, SR.net_webtransport_internal_error, ex);
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
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-4.6"/>
    internal void ReceiveDrain()
    {
        if (State == WebTransportSessionState.Open)
        {
            _ = GracefulShutdownAsync();
        }
    }

    /// <summary>
    /// Method for cleaning up the session after the closing handshake has been performed and the session has been marked as closed.
    /// </summary>
    /// <remarks>May be called multiple times. All callers await the same cleanup task.</remarks>
    /// <param name="errorCodeForStreams">Error code used to abort all <see cref="QuicStream"/> instances associated with this session.</param>
    private ValueTask CleanUpSessionAsync(Http3ErrorCode errorCodeForStreams)
    {
        // Fast path: if cleanup already started, just await the existing task without taking the lock.
        Task? task = _cleanUpTask;
        if (task is not null)
        {
            return new ValueTask(task);
        }

        lock (SyncLock)
        {
            _cleanUpTask ??= CleanUpSessionCoreAsync(errorCodeForStreams);
            task = _cleanUpTask;
        }
        return new ValueTask(task);
    }

    private async Task CleanUpSessionCoreAsync(Http3ErrorCode errorCodeForStreams)
    {
        bool tryClosingGracefully;
        lock (SyncLock)
        {
            tryClosingGracefully = State == WebTransportSessionState.ClosedLocally;
        }

        await CleanUpOpenOutboundWaitersAsync().ConfigureAwait(false);
        await CleanUpPendingAndOpenStreamsAndCloseConnectStreamAsync(errorCodeForStreams, tryClosingGracefully).ConfigureAwait(false);
        await _sessionManager.RemoveSessionAsync(_connectStream).ConfigureAwait(false);
        _outboundUnidirectionalStreamSemaphore.Dispose();
        _outboundBidirectionalStreamSemaphore.Dispose();
        _outboundOpenShutdownCts.Dispose();

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
        ValueTask uniStreamsTask = WebTransportPendingStreamCleanup.CloseAndDisposeAllStreamsInChannelAsync(_pendingUnidirectionalStreams, httpErrorCodeForPendingStreams);
        ValueTask biStreamsTask = WebTransportPendingStreamCleanup.CloseAndDisposeAllStreamsInChannelAsync(_pendingBidirectionalStreams, httpErrorCodeForPendingStreams);

        await uniStreamsTask.ConfigureAwait(false);
        await biStreamsTask.ConfigureAwait(false);

        _pendingUnidirectionalStreams = null;
        _pendingBidirectionalStreams = null;
    }
}
