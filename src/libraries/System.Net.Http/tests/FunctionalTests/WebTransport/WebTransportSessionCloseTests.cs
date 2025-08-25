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

    private void AssertStreamIsClosedWithSpinWait(WebTransportStream stream)
    {
        SpinWait.SpinUntil(() => stream.WritesClosed.IsCompleted, 3000);
        Assert.True(stream.WritesClosed.IsCompleted);

        SpinWait.SpinUntil(() => stream.ReadsClosed.IsCompleted, 3000);
        Assert.True(stream.ReadsClosed.IsCompleted);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(s_errorMessagesAsParameters))]
    public async Task SessionCloseAsyncSendsCorrectCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
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
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.CloseAsync(expectedApplicationErrorCode, expectedApplicationErrorMessage);
            await Task.WhenAll(serverTask); // prevent client from closing connect stream
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(s_errorMessagesAsParameters))]
    public async Task ClientClosesSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);
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
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ClosesClientSessionAfterServerClosesConnectStream()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            serverSession.ConnectStream.CompleteWrites();
            await Task.WhenAll(clientTask); // prevent server from closing the session
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ServerGracefullyClosesConnectStreamResultsInAllOtherStreamsBeingClosed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.CompleteWrites();

            await Task.WhenAll(clientTask); // prevent server from closing the session
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(QuicAbortDirection.Write)]
    [InlineData(QuicAbortDirection.Read)]
    [InlineData(QuicAbortDirection.Both)]
    public async Task ServerAbortivelyClosesConnectStreamResultsInAllOtherStreamsBeingClosed(QuicAbortDirection abortDirection)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.Abort(abortDirection, 0);

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ClientClosesAllStreamsInSessionAfterReceivingCloseSessionCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
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

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);
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

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SessionRequestCloseAsyncSendsCorrectCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_drainSessionCapsuleCode, capsuleCode);
            Assert.Equal(0, capsuleValueLength);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            await session.RequestCloseAsync();

            await Task.WhenAll(serverTask); // prevent client from closing connect stream
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAndAllStreamsAfterReceivingDrainSessionCapsuleWhenDefaultHandlerIsUsed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);
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

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHadnlerAfterReceivingDrainSessionCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        bool wasHandlerCalled = false;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(
                server.Address,
                client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalled = true; return Task.CompletedTask; } }
                );

            SpinWait.SpinUntil(() => wasHandlerCalled, 3000);

            Assert.True(wasHandlerCalled);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, 0);

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ReceiveDrainCapsuleWithInvalidValueClosesSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            int invalidLength = 1;
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(s_minValidSizeOfCloseSessionCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfCloseSessionCapsuleValue + 1)]
    public async Task ReceiveCloseSessionCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAndAllStreamsAfterReceivingGoAwayFrameWhenDefaultHandlerIsUsed()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream);
            AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            await serverSession.Connection.ShutdownAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHadnlerAfterReceivingGoAwayFrame()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        bool wasHandlerCalled = false;
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(
                server.Address,
                client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalled = true; return Task.CompletedTask; } }
                );

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => wasHandlerCalled, 3000);

            Assert.True(wasHandlerCalled);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            _ = serverSession.Connection.ShutdownAsync(); // don't await, it will complete only after the client closes the connection completely

            await Task.WhenAll(clientTask);
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

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusCode);
            Assert.Null(session.CloseStatusDescription);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.DisposeAsync(); // This will close the underlying QUIC connection, which will result in the CONNECT stream being closed

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task AllOperationsThrowWebTransportExceptionOnClosedSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            session.Close();

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            // All operations should throw WebTransportException
            await Assert.ThrowsAsync<WebTransportException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.SetMaxDataSentLimitForPeerAsync(1));
            await Assert.ThrowsAsync<WebTransportException>(() => session.RequestCloseAsync());
            await Assert.ThrowsAsync<WebTransportException>(() => session.CloseAsync(1, ""));
            Assert.Throws<WebTransportException>(() => session.Close());
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
    // TODO: write tests for operation cancellations
    // TODO: write tests that send GOAWAY frame
}
