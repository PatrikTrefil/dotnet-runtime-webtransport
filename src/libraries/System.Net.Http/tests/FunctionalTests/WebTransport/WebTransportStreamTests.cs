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
using System.IO;
using System.Threading;

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

    [Theory]
    [MemberData(nameof(AbortTestParameters))]
    public async void ClientAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            await AssertReadOperationsAndReadsClosedOnStreamThrowAsync<QuicException>(
                clientInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ErrorCodeRemapping.HttpCodeToWebTransportCode((long)ex.ApplicationErrorCode))
                );
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(WebTransportAbortDirection.Write, expectedWebTransportErrorCode);

            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(AbortTestParameters))]
    public async void ClientAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            await AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<QuicException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ErrorCodeRemapping.HttpCodeToWebTransportCode((long)ex.ApplicationErrorCode))
                );
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(WebTransportAbortDirection.Read, expectedWebTransportErrorCode);

            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    private static readonly uint[] _errorCodes = [0, 10, int.MaxValue, uint.MaxValue];
    public static IEnumerable<object[]> AbortTestParameters()
    {
        foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (uint errorCode in _errorCodes)
            {
                yield return new object[] { streamType, errorCode };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AbortTestParameters))]
    public async void ServerAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<WebTransportStreamClosedException>(
                clientInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ex.ApplicationErrorCode)
                );
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(QuicAbortDirection.Read, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(AbortTestParameters))]
    public async void ServerAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportStreamClosedException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ex.ApplicationErrorCode)
                );
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(QuicAbortDirection.Write, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void DisposedStreamTest(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            WebTransportStream serverInitiatedStream;
            using (serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType)) { }

            await AssertAllOperationsOnStreamThrowAsync<ObjectDisposedException>(serverInitiatedStream, exceptionValidator: null);
            Assert.False(serverInitiatedStream.CanRead);
            Assert.False(serverInitiatedStream.CanWrite);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async void NotSupportedOperationsThrows(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<NotSupportedException>(() => serverInitiatedStream.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => serverInitiatedStream.SetLength(10));
            Assert.Throws<NotSupportedException>(() => { long position = serverInitiatedStream.Position; });
            Assert.Throws<NotSupportedException>(() => { long length = serverInitiatedStream.Length; });
            Assert.Throws<NotSupportedException>(() => { serverInitiatedStream.Position = 1; });

            if (streamType == WebTransportStreamType.Unidirectional)
            {
                await AssertWriteOperationsOnStreamThrowAsync<NotSupportedException>(serverInitiatedStream, exceptionValidator: null);
            }
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    async Task AssertAllOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    async Task AssertWriteOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(() => stream.WriteAsync(new byte[1]).AsTask()),
            Assert.Throws<TException>(() => stream.WriteByte(2)),
            Assert.Throws<TException>(() => stream.Write(new byte[1])),
        ];
        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    async Task AssertReadOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(() => stream.ReadAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.ReadAtLeastAsync(new byte[1], 1).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.ReadExactlyAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.CopyToAsync(new MemoryStream(), 10)),
            Assert.Throws<TException>(() => stream.CopyTo(new MemoryStream(), 10)),
            Assert.Throws<TException>(() => stream.ReadByte()),
            Assert.Throws<TException>(() => stream.Read(new byte[1])),
            Assert.Throws<TException>(() => stream.ReadExactly(new byte[1]))
        ];
        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException: Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync((Stream)stream, exceptionValidator);
    }

    async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException: Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync((Stream)stream, exceptionValidator);
    }

    async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException: Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync((Stream)stream, exceptionValidator);
    }

    async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException: Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync((Stream)stream, exceptionValidator);
    }

    // TODO: add tests for cancellations of stream operations
}

