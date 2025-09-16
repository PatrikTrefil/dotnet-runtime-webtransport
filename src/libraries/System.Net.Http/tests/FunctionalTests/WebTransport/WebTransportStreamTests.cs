// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.IO;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(WebTransportTestBase.IsWebTransportSupported))]
public sealed class WebTransportStreamTests : WebTransportTestBase
{
    private const int TestTimeout = 200_000;

    private static readonly uint[] _errorCodes = [0, 10, int.MaxValue, uint.MaxValue];
    public static readonly TheoryData<WebTransportStreamType, long> s_abortTestParameters = AbortTestParameters();
    private static TheoryData<WebTransportStreamType, long> AbortTestParameters()
    {
        var theoryData = new TheoryData<WebTransportStreamType, long>();
        foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (uint errorCode in _errorCodes)
            {
                theoryData.Add(streamType, errorCode);
            }
        }
        return theoryData;
    }

    public static readonly TheoryData<byte[]> s_dataToSend = new TheoryData<byte[]>([
        [1],
        [1, 2, 3]
    ]);

    public static readonly TheoryData<byte[], WebTransportStreamType> s_dataToSendWithStreamType = DataToSendWithStreamType();

    private static TheoryData<byte[], WebTransportStreamType> DataToSendWithStreamType()
    {
        var theoryData = new TheoryData<byte[], WebTransportStreamType>();
        foreach (byte[] dataToSend in s_dataToSend)
        {
            foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
            {
                theoryData.Add(dataToSend, streamType);
            }
        }
        return theoryData;
    }

    public WebTransportStreamTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ClientOpensStream(WebTransportStreamType streamType)
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
    public async Task ServerOpensStream(WebTransportStreamType streamType)
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
    [MemberData(nameof(s_dataToSendWithStreamType))]
    public async Task SendDataFromClientToServerOverClientInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);
            clientInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataFromServerToClientOverClientInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);
            stream.Write(data);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await stream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataFromClientToServerOverServerInitiatedStream(byte[] data)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            serverInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_dataToSendWithStreamType))]
    public async Task SendDataFromServerToClientOverServerInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            serverInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]

    public async Task AbortStreamWithInvalidAbortDirectionThrows(WebTransportStreamType streamType)
    {
        var invalidAbortDirection = (WebTransportAbortDirection)42;

        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>("abortDirection", () => serverInitiatedStream.Abort(invalidAbortDirection, 0));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ClientAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
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

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(WebTransportAbortDirection.Write, expectedWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ClientAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
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

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(WebTransportAbortDirection.Read, expectedWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ServerAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<WebTransportException>(
                clientInitiatedStream,
                exceptionValidator: (ex) =>
                {
                    Assert.Equal(WebTransportError.StreamAborted, ex.WebTransportError);
                    Assert.Equal(expectedWebTransportErrorCode, ex.ApplicationErrorCode);
                }
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(QuicAbortDirection.Read, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ServerAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportException>(
                serverInitiatedStream,
                exceptionValidator: (ex) =>
                {
                    Assert.Equal(WebTransportError.StreamAborted, ex.WebTransportError);
                    Assert.Equal(expectedWebTransportErrorCode, ex.ApplicationErrorCode);
                }
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(QuicAbortDirection.Write, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ServerAbortsStreamWriteSideAbortsWithIncorrectErrorCode(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new Barrier(2); // TODO: remove once we have RESET_STREAM_AT support

        const long maxValidErrorCode = 0x52e5ac983162;
        const long invalidWebTransportErrorCode = maxValidErrorCode + 1;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Null(ex.ApplicationErrorCode)
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(QuicAbortDirection.Write, invalidWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task DisposedStreamTest(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            WebTransportStream serverInitiatedStream;
            using (serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType)) { }

            await AssertAllOperationsOnStreamThrowAsync<ObjectDisposedException>(serverInitiatedStream, exceptionValidator: null);
            Assert.False(serverInitiatedStream.CanRead);
            Assert.False(serverInitiatedStream.CanWrite);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task NotSupportedOperationsThrows(WebTransportStreamType streamType)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

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

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task WritesCompleteIsCompletedInUnidirectionalStream()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await serverInitiatedStream.WritesClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task ReadsCompleteIsCompletedInUnidirectionalStream()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream serverInitiatedStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await serverInitiatedStream.ReadsClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await using QuicStream serverInitiatedStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            barrier.SignalAndWait();
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

    // TODO: add tests for when the client receives an invalid webtransport error code
    // TODO: add tests for cancellations of stream operations
}

