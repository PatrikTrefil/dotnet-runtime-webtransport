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
    public const string s_protocolPseudoHeaderValue = "webtransport";
    private readonly Http3LoopbackServer _httpServer;
    private bool _disposedValue;
    private List<WebTransportServerSession> _sessions = new List<WebTransportServerSession>();

    public Uri Address => _httpServer.Address;

    public WebTransportLoopbackServer(Http3LoopbackServer httpServer)
    {
        ArgumentNullException.ThrowIfNull(httpServer);

        _httpServer = httpServer;
    }

    public async Task<WebTransportServerSession> CreateWebTransportServerSessionAsync()
    {
        Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
            new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
            new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );
        HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);
        QuicStream controlStream = connection.CurrentStream.Stream;
        bool isValidOpeningHandshake = httpRequestData.Method == HttpMethod.Connect.ToString() && httpRequestData.GetSingleHeaderValue(":protocol") == s_protocolPseudoHeaderValue;

        Assert.True(isValidOpeningHandshake, "Invalid handshake from client received");

        await connection.SendResponseAsync(content: null, isFinal: false);

        WebTransportServerSession session = new() { Connection = connection, ConnectStream = controlStream };
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
