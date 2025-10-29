// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Net.Quic;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;
using ChannelItem = (System.Net.ArrayBuffer ArrayBuffer, System.Net.Quic.QuicStream QuicStream);
using System.Collections.Generic;

namespace System.Net.WebTransport;


internal sealed class MsQuicWebTransportExtendedConnectManager : Http3ExtendedConnectManager, IMsQuicWebTransportSessionConnectionManager
{
    private long _maxSessionsCount;
    private long _openSessionsCount;
    private long _initialMaxUnidirectionalStreamsPerSession;
    private long _initialMaxBidirectionalStreamsPerSession;
    private long _initialMaxDataPerSession;
    // Under specific circumstances it's possible that the WebTransportSession object is created after a GOAWAY was received.
    // For this scenario we need to remember to call the graceful shutdown handler right after the session is created.
    // To check if the graceful shutdown handler needs to be called we use this boolean variable.
    private bool _wasGoAwayReceived;
    private Lock SessionCountsLock { get; } = new();
    private Lock DictionaryLock { get; } = new();
    private readonly Dictionary<long, DictionaryItem> _idSessionAndChannelsDict = new();

    private Lock SyncObjSettingsValidation { get; } = new();
    private bool _isSettingsValidationDone;
    private Exception? _validationException;

    public MsQuicWebTransportExtendedConnectManager(Http3ExtendedConnectManagerCreationOptions options) : base(options) { }

    public override async Task ProcessGoAwayAsync()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Task[] goAwayHandlerTasks;
        lock (DictionaryLock)
        {
            goAwayHandlerTasks = new Task[_idSessionAndChannelsDict.Count];
            int i = 0;
            foreach (DictionaryItem dictionaryItem in _idSessionAndChannelsDict.Values)
            {

                goAwayHandlerTasks[i] = dictionaryItem switch
                {
                    SessionAndChannels sessionAndChannels => Task.Run(async () =>
                        {
                            if (sessionAndChannels.Session != null)
                            {
                                await sessionAndChannels.Session.GracefulShutdownAsync().ConfigureAwait(false);
                            }
                        }),
                    Tombstone => Task.CompletedTask,
                    _ => throw new InvalidOperationException("Unknown dictionary item type")
                };
                i++;
            }

            _wasGoAwayReceived = true;
        }

        await Task.WhenAll(goAwayHandlerTasks).ConfigureAwait(false);
    }

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-stream-type-registration"/>
    public override long UnidirectionalStreamType => 0x54;

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-4.2-4"/>
    public override long BidirectionalStreamSignalValue => 0x41;

    public WebTransportSession CreateSession(QuicStream connectStream, byte[] connectStreamBuffer, QuicConnection quicConnection, Func<WebTransportSession, Task> gracefulShutdownHandler, string? subprotocol, long defaultStreamErrorCode)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Debug.Assert(_isSettingsValidationDone == true);

        long sessionId = connectStream.Id;

        // It's possible that a there are already pending streams for the session we are creating
        SessionAndChannels sessionAndChannels;
        bool shouldCallGracefulShutdownHandler = false;
        lock (DictionaryLock)
        {
            if (!_idSessionAndChannelsDict.TryGetValue(sessionId, out DictionaryItem? dictionaryItem))
            {
                sessionAndChannels = new SessionAndChannels(ChannelItemDropped);
                _idSessionAndChannelsDict[sessionId] = sessionAndChannels;
                shouldCallGracefulShutdownHandler = _wasGoAwayReceived;
            }
            else
            {
                Debug.Assert(dictionaryItem != null);
                Debug.Assert(dictionaryItem is SessionAndChannels);
                sessionAndChannels = (SessionAndChannels)dictionaryItem;
            }
        }

        Debug.Assert(sessionAndChannels.Session == null, "Session object should only be created once per CONNECT stream");

        sessionAndChannels.Session = new MsQuicWebTransportSession(
            sessionId,
            this,
            connectStream,
            connectStreamBuffer,
            sessionAndChannels.PendingUnidirectionalStreams,
            sessionAndChannels.PendingBidirectionalStreams,
            gracefulShutdownHandler,
            subprotocol,
            defaultStreamErrorCode)
        {
            DataSentLimitProvidedByPeer = _initialMaxDataPerSession,
            UnidirectionalStreamCountLimitProvidedByPeer = _initialMaxUnidirectionalStreamsPerSession,
            BidirectionalStreamCountLimitProvidedByPeer = _initialMaxBidirectionalStreamsPerSession
        };

        sessionAndChannels.Session.Init();

        if (shouldCallGracefulShutdownHandler)
        {
            _ = sessionAndChannels.Session.GracefulShutdownAsync();
        }

        return sessionAndChannels.Session;
    }

    public override async Task ProcessReceivedStreamAsync(QuicStreamType streamType, ArrayBuffer buffer, QuicStream stream)
    {
        int bytesRead;
        long sessionId;
        while (!VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan, out sessionId, out bytesRead))
        {
            buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength);
            bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                sessionId = -1;
                break;
            }

            buffer.Commit(bytesRead);
        }
        buffer.Discard(bytesRead);

        ProcessReceivedStreamForSessionAsync(streamType, buffer, stream, sessionId);
    }

    private void ProcessReceivedStreamForSessionAsync(QuicStreamType streamType, ArrayBuffer buffer, QuicStream stream, long sessionId)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"Stream received for session {sessionId}");

        lock (DictionaryLock)
        {
            // The session may not exist yet. In that case we only create the channels without a session object.
            // The session object will then attached in the CreateSession method
            if (!_idSessionAndChannelsDict.TryGetValue(sessionId, out DictionaryItem? dictionaryItem))
            {
                dictionaryItem = new SessionAndChannels(ChannelItemDropped);
                _idSessionAndChannelsDict[sessionId] = dictionaryItem;
            }
            else
            {
                Debug.Assert(dictionaryItem != null);
            }

            if (dictionaryItem is Tombstone)
            {
                RejectReceivedStreamForClosedSession(buffer, stream);
                return;
            }

            SessionAndChannels sessionAndChannels = (SessionAndChannels)dictionaryItem;

            Channel<ChannelItem> channelForStreamType = streamType switch
            {
                QuicStreamType.Unidirectional => sessionAndChannels.PendingUnidirectionalStreams,
                QuicStreamType.Bidirectional => sessionAndChannels.PendingBidirectionalStreams,
                _ => throw new ArgumentException("Unknown stream type", nameof(streamType))
            };

            bool wasWriteSuccessful = channelForStreamType.Writer.TryWrite((buffer, stream));
            if (!wasWriteSuccessful) // session has been closed
            {
                RejectReceivedStreamForClosedSession(buffer, stream);
            }
        }
    }

    private static void RejectReceivedStreamForClosedSession(ArrayBuffer buffer, QuicStream stream)
    {
        buffer.Dispose();
        stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
        stream.Dispose();
    }

    public override void ValidateAndProcessServerSettings(Dictionary<long, long> serverSettings)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Debug.Assert(serverSettings != null);

        lock (SyncObjSettingsValidation)
        {
            if (_isSettingsValidationDone)
            {
                if (_validationException != null)
                {
                    throw _validationException;
                }
            }

            try
            {
                ValidateAndProcessServerSettingsCore(serverSettings);
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);

                _validationException = e;
                throw;
            }
            finally
            {
                _isSettingsValidationDone = true;
            }
        }
    }

    private void ValidateAndProcessServerSettingsCore(Dictionary<long, long> serverSettings)
    {
        bool maxSessionsSettingRetrievalSuccess = serverSettings.TryGetValue((long)Http3SettingType.WebTransportMaxSessions, out long value);

        if (!maxSessionsSettingRetrievalSuccess || value == 0)
        {
            throw new WebTransportException(WebTransportError.HeaderError, SR.net_webtransport_server_does_not_support_webtransport_over_http3);
        }

        _initialMaxUnidirectionalStreamsPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxUnidirectionalStreamsPerSession, 0);
        _initialMaxBidirectionalStreamsPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxBidirectionalStreamsPerSession, 0);
        _initialMaxDataPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxDataPerSession, 0);

        _maxSessionsCount = value;
    }

    void IMsQuicWebTransportSessionConnectionManager.RemoveSession(QuicStream connectStream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        bool wasRemovalSuccessful;
        long sessionId = connectStream.Id;
        lock (DictionaryLock)
        {
            _idSessionAndChannelsDict.TryGetValue(sessionId, out DictionaryItem? dictionaryItem);
            if (dictionaryItem is Tombstone or null)
            {
                wasRemovalSuccessful = false;
            }
            else
            {
                _idSessionAndChannelsDict[sessionId] = Tombstone.Instance;
                wasRemovalSuccessful = true;
            }
        }

        if (wasRemovalSuccessful)
        {
            lock (SessionCountsLock)
            {
                _openSessionsCount--;
            }

            RemoveSessionAsync(connectStream);

            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"Removed session with ID {sessionId}.");
        }
    }

    public override void ReserveSession()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SessionCountsLock)
        {
            if (_openSessionsCount == _maxSessionsCount)
            {
                throw new WebTransportException(WebTransportError.SessionConnectFailure, SR.net_webtransport_maximum_number_of_sessions_reached);
            }
            _openSessionsCount++;
        }
    }

    public override void ReleaseSessionAfterFailedHandshake(QuicStream? quicStream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SessionCountsLock)
        {
            _openSessionsCount--;
        }
        if (quicStream != null)
        {
            RemoveSessionAsync(quicStream);
        }
    }

    private void ChannelItemDropped(ChannelItem channelItem)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"Rejecting stream {channelItem.QuicStream}.");

        QuicStream stream = channelItem.QuicStream;
        stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebTransportBufferedStreamRejected);
        stream.Dispose();
        channelItem.ArrayBuffer.Dispose();
    }

    async Task<QuicStream> IMsQuicWebTransportSessionConnectionManager.OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        return await OpenOutboundStreamAsync(type, cancellationToken).ConfigureAwait(false);
    }

    void IMsQuicWebTransportSessionConnectionManager.RemoveOutboundStream()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        RemoveOutboundStream();
    }

    private abstract class DictionaryItem { }

    private sealed class Tombstone : DictionaryItem
    {
        public static Tombstone Instance { get; } = new Tombstone();
        private Tombstone() { }
    }

    private sealed class SessionAndChannels : DictionaryItem
    {
        private const int s_maxPendingUnidirectionalStreams = 100;
        private const int s_maxPendingBidirectionalStreams = 100;

        public MsQuicWebTransportSession? Session { get; set; }
        public Channel<ChannelItem> PendingUnidirectionalStreams { get; }
        public Channel<ChannelItem> PendingBidirectionalStreams { get; }

        public SessionAndChannels(Action<ChannelItem> channelItemDropped)
        {
            PendingUnidirectionalStreams = Channel.CreateBounded(
                new BoundedChannelOptions(s_maxPendingUnidirectionalStreams) { FullMode = BoundedChannelFullMode.DropNewest },
                channelItemDropped
            );
            PendingBidirectionalStreams = Channel.CreateBounded(
                new BoundedChannelOptions(s_maxPendingBidirectionalStreams) { FullMode = BoundedChannelFullMode.DropNewest },
                channelItemDropped
            );
        }
    }
}
