// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Net.Quic;
using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Linq;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionTests : WebTransportTestBase
{
    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async void ConnectionEstablishmentWithValidHandshakeSucceeds()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void ClientOpensStream(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType, serverSession);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void ServerOpensStream(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType, serverSession);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }


    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(DataToSendWithStreamType))]
    public async void SendDataFromClientToServerOverClientInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType, serverSession);
            byte[] receivedData = new byte[data.Length];
            clientInitiatedStream.ReadExactly(receivedData);
            Assert.Equal(data, receivedData);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);
            clientInitiatedStream.Write(data);
            await serverTask; // prevent stream reset before the data is read by the server
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(DataToSendAsParameters))]
    public async void SendDataFromServerToClientOverClientInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType, serverSession);
            stream.Write(data);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            stream.ReadExactly(receivedData);
            Assert.Equal(data, receivedData);
            await serverTask; // prevent stream reset before the data is read by the server
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(DataToSendAsParameters))]
    public async void SendDataFromClientToServerOverServerInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType, serverSession);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            serverInitiatedStream.Write(data);
            await serverTask; // prevent stream reset before the data is read by the server
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(DataToSendWithStreamType))]
    public async void SendDataFromServerToClientOverServerInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType, serverSession);
            serverInitiatedStream.Write(data);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            serverInitiatedStream.ReadExactly(receivedData);
            Assert.Equal(data, receivedData);
            await serverTask; // prevent stream reset before the data is read by the server
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    private static readonly IEnumerable<byte[]> _dataToSendRaw = [
        [1],
        [1, 2, 3]
    ];
    private static readonly IEnumerable<object[]> _dataToSendAsParameters = _dataToSendRaw.Select(item => new object[] { item });
    public static IEnumerable<object[]> DataToSendAsParameters => _dataToSendAsParameters;

    public static IEnumerable<object[]> DataToSendWithStreamType()
    {
        foreach (byte[] dataToSend in _dataToSendRaw)
        {
            foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
            {
                yield return new object[] { dataToSend, streamType };
            }
        }
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async void ObjectDisposedExceptionIsThrownWhenAccessingPropertiesOfDisposedSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            WebTransportSession session;
            await using (session = await WebTransportSession.ConnectAsync(server.Address, client)) { }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetMaxDataSentLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RequestCloseAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""u8.ToArray()));
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task InvalidVariableLengthIntegerPassedToSessionConfigurationPropertiesThrows(long invalidVarInt)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetMaxDataSentLimitForPeerAsync(invalidVarInt));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await clientTask; // prevent server session from closing before client task runs
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxUnidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxBidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxData = invalidVarInt });
    }
    // TODO: write more tests
}
