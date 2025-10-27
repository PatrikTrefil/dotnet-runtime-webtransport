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
    private const long s_defaultMaxSessionCount = VariableLengthIntegerHelper.MaxValue; // No limit by default
    private readonly Http3LoopbackServer _httpServer;
    private bool _disposedValue;
    private List<WebTransportServerSession> _sessions = new List<WebTransportServerSession>();
    private readonly long _defaultMaxSessionCount;

    public Uri Address => _httpServer.Address;

    public WebTransportLoopbackServer(Http3LoopbackServer httpServer, long defaultMaxSessionCount = s_defaultMaxSessionCount)
    {
        ArgumentNullException.ThrowIfNull(httpServer);

        _httpServer = httpServer;
        _defaultMaxSessionCount = defaultMaxSessionCount;
    }

    public async Task<WebTransportServerSession> AcceptHttpConnectionAndWebTransportServerSessionAsync(string? subprotocolToRespondWith = null)
    {
        return await AcceptHttpConnectionAndWebTransportServerSessionAsync(_defaultMaxSessionCount, subprotocolToRespondWith);
    }

    public async Task<WebTransportServerSession> AcceptHttpConnectionAndWebTransportServerSessionAsync(long maxSessionCount, string? subprotocolToRespondWith = null)
    {
        Http3LoopbackConnection connection = await AcceptWebTransportEnabledConnection(maxSessionCount);

        return await AcceptWebTransportServerSessionAsync(connection, subprotocolToRespondWith);
    }

    private async Task<Http3LoopbackConnection> AcceptWebTransportEnabledConnection(long maxSessionCount)
    {
        return await _httpServer.EstablishConnectionAsync(
            new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
            new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = maxSessionCount }
            );
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
