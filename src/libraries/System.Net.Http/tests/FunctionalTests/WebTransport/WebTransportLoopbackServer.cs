// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Collections.Generic;

namespace System.Net.WebTransport.Functional.Tests;

internal sealed class WebTransportLoopbackServer : IAsyncDisposable
{
    private const string s_protocolPseudoHeaderValue = "webtransport";
    private readonly Http3LoopbackServer _httpServer;
    private bool _disposedValue;
    private List<WebTransportServerSession> _sessions = new List<WebTransportServerSession>();
    private readonly WebTransportHttpConnectionCreationOptions _defaultOptions;

    public Uri Address => _httpServer.Address;

    public WebTransportLoopbackServer(Http3LoopbackServer httpServer) : this(
        httpServer,
        new WebTransportHttpConnectionCreationOptions
        {
            // No limits by default
            MaxSessionCount = VariableLengthIntegerHelper.MaxValue,
            InitialDataSentLimitForPeer = VariableLengthIntegerHelper.MaxValue,
        })
    { }

    public WebTransportLoopbackServer(Http3LoopbackServer httpServer, WebTransportHttpConnectionCreationOptions defaultOptions)
    {
        ArgumentNullException.ThrowIfNull(httpServer);

        _httpServer = httpServer;
        _defaultOptions = defaultOptions;
    }

    public async Task<WebTransportServerSession> AcceptHttpConnectionAndWebTransportServerSessionAsync(string? subprotocolToRespondWith = null)
    {
        return await AcceptHttpConnectionAndWebTransportServerSessionAsync(_defaultOptions, subprotocolToRespondWith);
    }

    public async Task<WebTransportServerSession> AcceptHttpConnectionAndWebTransportServerSessionAsync(WebTransportHttpConnectionCreationOptions options, string? subprotocolToRespondWith = null)
    {
        Http3LoopbackConnection connection = await AcceptWebTransportEnabledConnection(options);

        return await AcceptWebTransportServerSessionAsync(connection, subprotocolToRespondWith);
    }

    private async Task<Http3LoopbackConnection> AcceptWebTransportEnabledConnection(WebTransportHttpConnectionCreationOptions options)
    {
        List<Http3SettingsEntry> settings = [
            new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
            new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = options.MaxSessionCount }
            ];

        if (options.InitialUnidirectionalStreamCountLimitForPeer != 0)
        {
            settings.Add(new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportInitialMaxUnidirectionalStreamsPerSession, Value = options.InitialUnidirectionalStreamCountLimitForPeer });
        }
        if (options.InitialBidirectionalStreamCountLimitForPeer != 0)
        {
            settings.Add(new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportInitialMaxBidirectionalStreamsPerSession, Value = options.InitialBidirectionalStreamCountLimitForPeer });
        }
        if (options.InitialDataSentLimitForPeer != 0)
        {
            settings.Add(new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportInitialMaxDataPerSession, Value = options.InitialDataSentLimitForPeer });
        }

        return await _httpServer.EstablishConnectionAsync(settings.ToArray());
    }

    public async Task<WebTransportServerSession> AcceptWebTransportServerSessionAsync(Http3LoopbackConnection connection, string? subprotocolToRespondWith = null)
    {
        HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);
        QuicStream connectStream = connection.CurrentStream.Stream;
        bool isValidOpeningHandshake = httpRequestData.Method == HttpMethod.Connect.ToString() && httpRequestData.GetSingleHeaderValue(":protocol") == s_protocolPseudoHeaderValue;

        Assert.True(isValidOpeningHandshake, "Invalid handshake from client received");

        List<HttpHeaderData> headers = [];
        if (subprotocolToRespondWith != null)
        {
            headers.Add(new HttpHeaderData("WT-Protocol", subprotocolToRespondWith));
        }

        await connection.SendResponseAsync(content: null, headers: headers, isFinal: false);

        WebTransportServerSession session = new() { Connection = connection, ConnectStream = connectStream };
        _sessions.Add(session);

        return session;
    }

    private async ValueTask DisposeAsyncCore(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                foreach (WebTransportServerSession session in _sessions)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }

            _disposedValue = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore(disposing: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
