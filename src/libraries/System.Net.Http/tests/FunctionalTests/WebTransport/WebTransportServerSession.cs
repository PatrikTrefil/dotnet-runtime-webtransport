// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.WebTransport.Functional.Tests;

internal sealed class WebTransportServerSession : IAsyncDisposable
{
    private static readonly byte[] s_unidirectionalStreamTypeEncodedAsVariableLengthInteger = [0x40, 0x54];

    private static readonly byte[] s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger = [0x40, 0x41];
    public long SessionId => ConnectStream.Id;
    public Http3LoopbackConnection Connection { get; init; }
    public QuicStream ConnectStream { get; init; }

    /// <summary>
    /// Dispose the CONNECT stream.
    /// </summary>
    public async ValueTask DisposeAsync() => await ConnectStream.DisposeAsync();

    public async Task<QuicStream> AcceptStreamFromServerAsync(WebTransportStreamType streamType)
    {
        QuicStream clientInitatedStream = await Connection.AcceptQuicStreamAsync();

        byte[] expectedStreamTypeOrSignalValueValue = streamType switch
        {
            WebTransportStreamType.Unidirectional => s_unidirectionalStreamTypeEncodedAsVariableLengthInteger,
            WebTransportStreamType.Bidirectional => s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger,
            _ => throw new ArgumentException("Unknown stream type", nameof(streamType))
        };

        byte[] receivedStreamTypeOrSignalValue = new byte[expectedStreamTypeOrSignalValueValue.Length];

        clientInitatedStream.Read(receivedStreamTypeOrSignalValue);
        Assert.Equal(expectedStreamTypeOrSignalValueValue, receivedStreamTypeOrSignalValue);

        var (sessionId, _) = await VariableLengthIntegerStreamHelper.ReadAsync(clientInitatedStream);
        Assert.Equal(SessionId, sessionId);

        return clientInitatedStream;
    }

    public async Task<QuicStream> OpenStreamFromServerAsync(WebTransportStreamType streamType)
    {
        QuicStreamType quicStreamType = streamType switch
        {
            WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
            WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(nameof(streamType), "Invalid stream type")
        };

        QuicStream stream = await Connection.OpenQuicStreamAsync(quicStreamType);

        switch (streamType)
        {
            case WebTransportStreamType.Unidirectional:
                stream.Write(s_unidirectionalStreamTypeEncodedAsVariableLengthInteger);
                break;
            case WebTransportStreamType.Bidirectional:
                stream.Write(s_bidirectionalStreamSignalValueEncodedAsVariableLengthInteger);
                break;
        }

        VariableLengthIntegerStreamHelper.Write(stream, SessionId);
        stream.Flush();

        return stream;
    }
}
