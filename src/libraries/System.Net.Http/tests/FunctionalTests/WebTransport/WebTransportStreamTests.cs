// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Linq;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(WebTransportTestBase.IsWebTransportSupported))]
public sealed class WebTransportStreamTests : WebTransportTestBase
{
    public WebTransportStreamTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void ClientOpensStream(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void ServerOpensStream(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }


    [Theory]
    [MemberData(nameof(DataToSendWithStreamType))]
    public async void SendDataFromClientToServerOverClientInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
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

    [Theory]
    [MemberData(nameof(DataToSendAsParameters))]
    public async void SendDataFromServerToClientOverClientInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);
            stream.Write(data);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await stream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);
            await serverTask; // prevent stream reset before the data is read by the server
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(DataToSendAsParameters))]
    public async void SendDataFromClientToServerOverServerInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
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

    [Theory]
    [MemberData(nameof(DataToSendWithStreamType))]
    public async void SendDataFromServerToClientOverServerInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            serverInitiatedStream.Write(data);
            await Task.WhenAll(clientTask);
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

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]

    public async void AbortStreamWithInvalidAbortDirectionThrows(WebTransportStreamType streamType)
    {
        var invalidAbortDirection = (WebTransportAbortDirection)42;

        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>("abortDirection", () => serverInitiatedStream.Abort(invalidAbortDirection, 0));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    // TODO: test that an abort closes the stream as it should
    // TODO: try to write a test that fails because we don't have RESET_STREAM_AT
    // TODO: add tests for cancellations of stream operations
}

