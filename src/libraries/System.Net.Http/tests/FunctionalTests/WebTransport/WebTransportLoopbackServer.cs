// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;

namespace System.Net.WebTransport.Functional.Tests;

internal sealed class WebTransportLoopbackServer
{
    public const string s_protocolPseudoHeaderValue = "webtransport";
    public static async Task<WebTransportServerSession> EstablishWebTransportServerSessionAsync(Http3LoopbackServer server)
    {
        Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
            new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
            new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );
        HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);
        QuicStream controlStream = connection.CurrentStream.QuicStream;
        bool isValidOpeningHandshake = httpRequestData.Method == HttpMethod.Connect.ToString() && httpRequestData.GetSingleHeaderValue(":protocol") == s_protocolPseudoHeaderValue;
        Assert.True(isValidOpeningHandshake, "Invalid handshake from client received");
        await connection.SendResponseAsync(content: null, isFinal: false);
        return new WebTransportServerSession { Connection = connection, ControlStream = controlStream };
    }
}
internal sealed class WebTransportServerSession : IAsyncDisposable
{
    private static byte[] s_unidirectionalStreamTypeEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x54 };

    private static byte[] s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger = new byte[] { 0x40, 0x41 };
    public long SessionId => ControlStream.Id;
    public Http3LoopbackConnection Connection { get; init; }
    public QuicStream ControlStream { get; init; }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

    public async Task<QuicStream> AcceptStreamFromServerAsync(WebTransportStreamType streamType, WebTransportServerSession serverSession)
    {

        QuicStream clientInitatedStream = await serverSession.Connection.AcceptQuicStreamAsync();
        byte[] expectedStreamTypeOrSignalValueValue = streamType switch
        {
            WebTransportStreamType.Unidirectional => s_unidirectionalStreamTypeEncodedAsVariableLengthInteger,
            WebTransportStreamType.Bidirectional => s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger,
            _ => throw new ArgumentException("Unknown stream type", nameof(streamType))
        };
        byte[] receivedStreamTypeOrSignalValue = new byte[expectedStreamTypeOrSignalValueValue.Length];
        clientInitatedStream.Read(receivedStreamTypeOrSignalValue);
        Assert.Equal(expectedStreamTypeOrSignalValueValue, receivedStreamTypeOrSignalValue);
        long sessionId = VariableLengthIntegerStreamHelper.Read(clientInitatedStream);
        Assert.Equal(serverSession.SessionId, sessionId);
        return clientInitatedStream;
    }

    public async Task<QuicStream> OpenStreamFromServerAsync(WebTransportStreamType streamType, WebTransportServerSession serverSession)
    {
        QuicStreamType quicStreamType = streamType switch
        {
            WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
            WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(nameof(streamType), "Invalid stream type")
        };
        QuicStream stream = await serverSession.Connection.OpenQuicStreamAsync(quicStreamType);
        switch (streamType)
        {
            case WebTransportStreamType.Unidirectional:
                stream.Write(s_unidirectionalStreamTypeEncodedAsVariableLengthInteger);
                break;
            case WebTransportStreamType.Bidirectional:
                stream.Write(s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger);
                break;
        }
        VariableLengthIntegerStreamHelper.Write(stream, serverSession.SessionId);
        stream.Flush();
        return stream;
    }

}
