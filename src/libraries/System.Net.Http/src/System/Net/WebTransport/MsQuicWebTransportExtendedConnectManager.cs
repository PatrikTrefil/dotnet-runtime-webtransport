// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Net.Quic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;

using ChannelItem = (System.Net.ArrayBuffer ArrayBuffer, System.Net.Quic.QuicStream QuicStream);
using System.Collections.Generic;

namespace System.Net.WebTransport;

internal sealed class MsQuicWebTransportExtendedConnectManager : Http3ExtendedConnectManager
{
    private readonly ConcurrentDictionary<long, SessionAndChannels> _idSessionAndChannelsDict = new();
    public MsQuicWebTransportExtendedConnectManager(Action disposedCallback) : base(disposedCallback) { }

    public override async Task GoAwayReceivedAsync()
    {
        Task[] goAwayHandlerTasks = new Task[_idSessionAndChannelsDict.Count];
        int i = 0;
        foreach (SessionAndChannels sessionAndChannels in _idSessionAndChannelsDict.Values)
        {
            goAwayHandlerTasks[i] = sessionAndChannels.Session?.GracefulShutdownHandler.Invoke() ?? Task.CompletedTask;
            i++;
        }
        await Task.WhenAll(goAwayHandlerTasks).ConfigureAwait(false);
    }

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-stream-type-registration"/>
    public override long UnidirectionalStreamType => 0x54;

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-4.2-4"/>
    public override long BidirectionalStreamSignalValue => 0x41;

    public WebTransportSession CreateSession(QuicStream connectStream, byte[] connectStreamBuffer, QuicConnection quicConnection, WebTransportSessionCreationOptions? options)
    {
        // It's possible that a there are already pending streams for the session we are creating
        SessionAndChannels sessionAndChannels = _idSessionAndChannelsDict.GetOrAdd(
            connectStream.Id,
             _ => new SessionAndChannels()
        );
        sessionAndChannels.Session = new MsQuicWebTransportSession(connectStream.Id, this, quicConnection, connectStream, connectStreamBuffer, sessionAndChannels.PendingUnidirectionalStreams, sessionAndChannels.PendingBidirectionalStreams, options);
        sessionAndChannels.Session.Init();
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

        // The session may not exist yet. In that case we only create the channels without a session object.
        // The session object will then attached in the CreateSession method
        SessionAndChannels sessionAndChannels = _idSessionAndChannelsDict.GetOrAdd(sessionId, _ => new SessionAndChannels());
        Channel<ChannelItem> channelForStreamType = streamType switch
        {
            QuicStreamType.Unidirectional => sessionAndChannels.PendingUnidirectionalStreams,
            QuicStreamType.Bidirectional => sessionAndChannels.PendingBidirectionalStreams,
            _ => throw new ArgumentException("Unknown stream type", nameof(streamType))
        };
        bool writeSuccess = channelForStreamType.Writer.TryWrite((buffer, stream));
        Debug.Assert(writeSuccess, $"Failed to add stream to {nameof(channelForStreamType)}");
    }

    public override void ValidateServerSettings(Dictionary<long, long> serverSettings)
    {
        ArgumentNullException.ThrowIfNull(serverSettings);
        bool success = serverSettings.TryGetValue((long)Http3SettingType.WebTransportMaxSessions, out long value);
        if (!success || value == 0)
        {
            throw new WebTransportException("Server does not support WebTransport over HTTP/3");
        }
    }

    private sealed class SessionAndChannels()
    {
        public MsQuicWebTransportSession? Session { get; set; }
        public Channel<ChannelItem> PendingUnidirectionalStreams { get; init; } = Channel.CreateUnbounded<ChannelItem>();
        public Channel<ChannelItem> PendingBidirectionalStreams { get; init; } = Channel.CreateUnbounded<ChannelItem>();
    }
}
