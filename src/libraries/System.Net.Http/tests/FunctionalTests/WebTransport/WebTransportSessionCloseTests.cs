// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net.Quic;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(WebTransportTestBase.IsWebTransportSupported))]
public sealed class WebTransportSessionCloseTests : WebTransportTestBase
{
    public WebTransportSessionCloseTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000_00;

    private const long s_closeSessionCapsuleCode = 0x2843;
    private const long s_drainSessionCapsuleCode = 0x78ae;

    private const int s_maxValidSizeOfCloseSessionCapsuleValue = 32 + 8192;
    private const int s_minValidSizeOfCloseSessionCapsuleValue = 32;

    private static readonly byte[][] s_errorMessages = [
        ""u8.ToArray(),
        "test errror message"u8.ToArray(),

    ];

    public static readonly IEnumerable<object[]> s_errorMessagesAsParameters = s_errorMessages.Select(item => new object[] { item });

    private async Task AssertStreamIsClosedWithSpinWait(WebTransportStream stream)
    {
        try
        {
            await stream.WritesClosed;
        } catch (Exception) { }


        try
        {
            await stream.ReadsClosed;
        } catch (Exception) { }
    }

    [Theory]
    [MemberData(nameof(s_errorMessagesAsParameters))]
    public async Task SessionCloseAsyncSendsCorrectCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);
        uint expectedApplicationErrorCode = 1;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Memory<byte> errorCodeBuffer = new byte[4];
            await serverSession.ConnectStream.ReadExactlyAsync(errorCodeBuffer); // TODO: use async reads everywhere
            uint receivedApplicationErrorCode = BinaryPrimitives.ReadUInt32BigEndian(errorCodeBuffer.Span);

            Memory<byte> messageBuffer = new byte[expectedApplicationErrorMessage.Length];
            await serverSession.ConnectStream.ReadExactlyAsync(messageBuffer);

            Assert.Equal(s_closeSessionCapsuleCode, capsuleCode);
            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
            Assert.Equal(sizeof(uint) + messageBuffer.Length, capsuleValueLength);
            Assert.Equal(expectedApplicationErrorMessage, messageBuffer);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.CloseAsync(expectedApplicationErrorCode, expectedApplicationErrorMessage);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_errorMessagesAsParameters))]
    public async Task ClientClosesSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_closeSessionCapsuleCode);
            Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, expectedApplicationErrorMessage.Length + applicationErrorCodeBuffer.Length);
            BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, expectedApplicationErrorCode);
            serverSession.ConnectStream.Write(applicationErrorCodeBuffer);
            serverSession.ConnectStream.Write(expectedApplicationErrorMessage);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task ClosesClientSessionAfterServerClosesConnectStream()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            serverSession.ConnectStream.CompleteWrites();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task ServerGracefullyClosesConnectStreamResultsInAllOtherStreamsBeingClosed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.CompleteWrites();

            barrier.SignalAndWait();
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(QuicAbortDirection.Write)]
    [InlineData(QuicAbortDirection.Read)]
    [InlineData(QuicAbortDirection.Both)]
    public async Task ServerAbortivelyClosesConnectStreamResultsInAllOtherStreamsBeingClosed(QuicAbortDirection abortDirection)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.Abort(abortDirection, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task ClientClosesAllStreamsInSessionAfterReceivingCloseSessionCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);
        byte[] expectedApplicationErrorMessage = "test error message"u8.ToArray();
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_closeSessionCapsuleCode);
            Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, expectedApplicationErrorMessage.Length + applicationErrorCodeBuffer.Length);
            BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, expectedApplicationErrorCode);
            serverSession.ConnectStream.Write(applicationErrorCodeBuffer);
            serverSession.ConnectStream.Write(expectedApplicationErrorMessage);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task SessionRequestCloseAsyncSendsCorrectCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_drainSessionCapsuleCode, capsuleCode);
            Assert.Equal(0, capsuleValueLength);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            await session.RequestCloseAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAndAllStreamsAfterReceivingDrainSessionCapsuleWhenDefaultHandlerIsUsed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHadnlerAfterReceivingDrainSessionCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);
        bool wasHandlerCalled = false;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(
                server.Address,
                client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalled = true; return Task.CompletedTask; } }
                );

            SpinWait.SpinUntil(() => wasHandlerCalled, TestTimeout);

            Assert.True(wasHandlerCalled);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task ReceiveDrainCapsuleWithInvalidValueClosesSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            int invalidLength = 1;
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [InlineData(s_minValidSizeOfCloseSessionCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfCloseSessionCapsuleValue + 1)]
    public async Task ReceiveCloseSessionCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAfterReceivingGoAwayFrameWhenDefaultHandlerIsUsed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.Connection.ShutdownAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHandlerAfterReceivingGoAwayFrame()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 1);
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(
                server.Address,
                client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(); return Task.CompletedTask; } }
                );

            barrier.SignalAndWait(); // Signal the session creation is completed

            await wasHandlerCalledSemaphore.WaitAsync(TestTimeout);

            barrier.SignalAndWait(); // Signal the handler was called
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            _ = serverSession.Connection.ShutdownAsync(); // don't await, it will complete only after the client closes the connection completely

            barrier.SignalAndWait(); // Wait for the handler to be called
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionWhenQuicConnectionIsClosed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusCode);
            Assert.Null(session.CloseStatusDescription);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.DisposeAsync(); // This will close the underlying QUIC connection, which will result in the CONNECT stream being closed

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Fact]
    public async Task AllOperationsThrowWebTransportExceptionOnClosedSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            session.Close();

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeout);

            // All operations should throw WebTransportException
            await Assert.ThrowsAsync<WebTransportException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetDataSentLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.RequestCloseAsync());
            await Assert.ThrowsAsync<WebTransportException>(() => session.CloseAsync(1, ""));
            Assert.Throws<WebTransportException>(() => session.Close());

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
}
