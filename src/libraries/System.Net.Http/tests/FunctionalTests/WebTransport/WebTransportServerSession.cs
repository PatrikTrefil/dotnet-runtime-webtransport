// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.WebTransport.Functional.Tests;

internal sealed class WebTransportServerSession : IAsyncDisposable
{
    public long SessionId => ConnectStream.Id;
    public Http3LoopbackConnection Connection { get; init; }
    public QuicStream ConnectStream { get; init; }

    /// <summary>
    /// Dispose the CONNECT stream.
    /// </summary>
    public async ValueTask DisposeAsync() => await ConnectStream.DisposeAsync();

    /// <summary>
    /// Accept the next incoming stream from the underlying QUIC connection and assert that it is a WebTransport stream of the expected type for addressed to this instance of <see cref="WebTransportServerSession"/>.
    /// </summary>
    /// <remarks>The implementation is not thread-safe.</remarks>
    /// <param name="expectedStreamTypeOfIncomingStream">The expected stream type.</param>
    public async Task<QuicStream> AcceptStreamFromServerAsync(WebTransportStreamType expectedStreamTypeOfIncomingStream)
    {
        QuicStream clientInitatedStream = await Connection.AcceptQuicStreamAsync();

        long expectedStreamTypeOrSignalValueValue = WebTransportStreamTypeHelper.GetStreamTypeOrSignalValue(expectedStreamTypeOfIncomingStream);

        (long StreamTypeOrSignalValue, _) = await VariableLengthIntegerStreamHelper.ReadAsync(clientInitatedStream);

        Assert.Equal(expectedStreamTypeOrSignalValueValue, StreamTypeOrSignalValue);

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

        long streamTypeOrSignalValue = WebTransportStreamTypeHelper.GetStreamTypeOrSignalValue(streamType);
        VariableLengthIntegerStreamHelper.Write(stream, streamTypeOrSignalValue);

        VariableLengthIntegerStreamHelper.Write(stream, SessionId);

        stream.Flush();

        return stream;
    }
}
