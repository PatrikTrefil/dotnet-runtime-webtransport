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
using System.Net.Http.Headers;

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
    private object SyncObjSessionCounts { get; } = new();
    private object SyncObjDictionary => _idSessionAndChannelsDict;
    private readonly Dictionary<long, DictionaryItem> _idSessionAndChannelsDict = new();

    private object SyncObjSettingsValidation { get; } = new();
    private bool _isSettingsValidationDone;
    private Exception? _validationException;

    public MsQuicWebTransportExtendedConnectManager(Http3ExtendedConnectManagerCreationOptions options) : base(options) { }

    public override async Task GoAwayReceivedAsync()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Task[] goAwayHandlerTasks;
        lock (SyncObjDictionary)
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
                                try
                                {
                                    await sessionAndChannels.Session.GracefulShutdownHandler.Invoke().ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);

                                    sessionAndChannels.Session.CloseOpenStreamsAndConnectStream(Http3ErrorCode.WebtransportSessionGone);
                                }
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

    public WebTransportSession CreateSession(QuicStream connectStream, byte[] connectStreamBuffer, QuicConnection quicConnection, Func<WebTransportSession, Task> gracefulShutdownHandler, string? subprotocol)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Debug.Assert(_isSettingsValidationDone == true);

        // It's possible that a there are already pending streams for the session we are creating
        DictionaryItem? dictionaryItem;
        bool shouldCallGracefulShutdownHandler = false;
        lock (SyncObjDictionary)
        {
            if (!_idSessionAndChannelsDict.TryGetValue(connectStream.Id, out dictionaryItem))
            {
                dictionaryItem = new SessionAndChannels();
                _idSessionAndChannelsDict[connectStream.Id] = dictionaryItem;
                shouldCallGracefulShutdownHandler = _wasGoAwayReceived;
            }
            else
            {
                Debug.Assert(dictionaryItem != null);
                if (dictionaryItem is SessionAndChannels sessionAndChannelsForCheck)
                {
                    Debug.Assert(sessionAndChannelsForCheck.Session == null, "Quic streams should have unique IDs per connection");
                }
            }
        }

        SessionAndChannels sessionAndChannels = (SessionAndChannels)dictionaryItem;

        Debug.Assert(sessionAndChannels.Session == null, "Session object should only be created once per CONNECT stream");

        sessionAndChannels.Session = new MsQuicWebTransportSession(
            connectStream.Id,
            this,
            connectStream,
            connectStreamBuffer,
            sessionAndChannels.PendingUnidirectionalStreams,
            sessionAndChannels.PendingBidirectionalStreams,
            gracefulShutdownHandler,
            subprotocol)
        {
            DataSentLimitProvidedByPeer = _initialMaxDataPerSession,
            UnidirectionalStreamCountLimitProvidedByPeer = _initialMaxUnidirectionalStreamsPerSession,
            BidirectionalStreamCountLimitProvidedByPeer = _initialMaxBidirectionalStreamsPerSession
        };

        sessionAndChannels.Session.Init();

        if (shouldCallGracefulShutdownHandler)
        {
            _ = sessionAndChannels.Session.GracefulShutdownHandler();
        }

        return sessionAndChannels.Session;
    }

    public override async Task StreamReceivedAsync(QuicStreamType streamType, ArrayBuffer buffer, QuicStream stream)
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

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"Stream received for session {sessionId}");

        lock (SyncObjDictionary)
        {
            // The session may not exist yet. In that case we only create the channels without a session object.
            // The session object will then attached in the CreateSession method
            if (!_idSessionAndChannelsDict.TryGetValue(sessionId, out DictionaryItem? dictionaryItem))
            {
                dictionaryItem = new SessionAndChannels();
                _idSessionAndChannelsDict[sessionId] = dictionaryItem;
            }
            else
            {
                Debug.Assert(dictionaryItem != null);
            }

            if (dictionaryItem is Tombstone)
            {
                stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
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
            if (!wasWriteSuccessful)
            {
                stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebTransportBufferedStreamRejected);
                FinishedUsingConnectStreamCallbackAsync(stream);
            }
        }
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
            throw new WebTransportException(WebTransportError.HeaderError, "Server does not support WebTransport over HTTP/3");
        }

        _initialMaxUnidirectionalStreamsPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxUnidirectionalStreamsPerSession, 0);
        _initialMaxBidirectionalStreamsPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxBidirectionalStreamsPerSession, 0);
        _initialMaxDataPerSession = serverSettings.GetValueOrDefault((long)Http3SettingType.WebTransportInitialMaxDataPerSession, 0);

        _maxSessionsCount = value;
    }

    /// <summary>
    /// Call when a session is closed and the CONNECT stream is no longer used.
    /// This method may be called multiple times for the same stream and is thread-safe.
    /// </summary>
    /// <param name="connectStream">CONNECT stream of the session to remove.</param>
    public void FinishedUsingConnectStream(QuicStream connectStream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        bool wasRemovalSuccessful;
        lock (SyncObjDictionary)
        {
            _idSessionAndChannelsDict.TryGetValue(connectStream.Id, out DictionaryItem? dictionaryItem);
            if (dictionaryItem is Tombstone or null)
            {
                wasRemovalSuccessful = false;
            }
            else
            {
                _idSessionAndChannelsDict[connectStream.Id] = Tombstone.Instance;
                wasRemovalSuccessful = true;
            }
        }

        if (wasRemovalSuccessful)
        {
            lock (SyncObjSessionCounts)
            {
                _openSessionsCount--;
            }

            FinishedUsingConnectStreamCallbackAsync(connectStream);

            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"Removed session with CONNECT stream {connectStream.Id}.");
        }
    }

    public override void BeforeExtendedConnectRequest()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncObjSessionCounts)
        {
            if (_openSessionsCount == _maxSessionsCount)
            {
                throw new WebTransportException(WebTransportError.SessionRefused, "Maximum number of allowed sessions reached");
            }
            _openSessionsCount++;
        }
    }

    public override void AfterFailedExtendedConnectRequest(QuicStream? quicStream)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (SyncObjSessionCounts)
        {
            _openSessionsCount--;
        }
        if (quicStream != null)
        {
            FinishedUsingConnectStreamCallbackAsync(quicStream);
        }
    }

    private abstract class DictionaryItem { }

    private sealed class Tombstone : DictionaryItem
    {
        public static Tombstone Instance { get; } = new Tombstone();
        private Tombstone() { }
    }

    private sealed class SessionAndChannels : DictionaryItem
    {
        // TODO: move to WebTransportCreationOptions
        private const int s_maxPendingUnidirectionalStreams = 10;
        private const int s_maxPendingBidirectionalStreams = 10;
        public MsQuicWebTransportSession? Session { get; set; }
        public Channel<ChannelItem> PendingUnidirectionalStreams { get; init; } = Channel.CreateBounded<ChannelItem>(s_maxPendingUnidirectionalStreams);
        public Channel<ChannelItem> PendingBidirectionalStreams { get; init; } = Channel.CreateBounded<ChannelItem>(s_maxPendingBidirectionalStreams);
    }
}
