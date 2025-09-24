// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Threading.Tasks;
using Xunit;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Net.Quic;
using System.Numerics;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write a test that makes gracefulshutdownhandler ignore the goaway and then check if we can do operations
// TODO: write test for gracefulshutdown that throws
// TODO: write test for the scenario: client opens a session and then closes it and then server tries to open a stream for the closed session
// TODO: write test for when a session is closed the session's streams are closed with the correct error code (might already be covered or maybe just needs to modify existing test)
// TODO: write test for opening more streams than can be pending

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionCloseTests : WebTransportTestBase
{
    public WebTransportSessionCloseTests(ITestOutputHelper output) : base(output) { }

    private const long s_closeSessionCapsuleCode = 0x2843;
    private const long s_drainSessionCapsuleCode = 0x78ae;

    private const int s_maxValidSizeOfCloseSessionCapsuleValue = 32 + 8192;
    private const int s_minValidSizeOfCloseSessionCapsuleValue = 32;

    public static readonly long[] s_applicationErrorCodesRaw = [0, 1, uint.MaxValue];
    public static readonly byte[][] s_errorMessagesRaw = [
        ""u8.ToArray(),
        "test errror message"u8.ToArray(),
    ];
    public static readonly TheoryData<byte[]> s_errorMessages = [.. s_errorMessagesRaw];

    public static readonly TheoryData<byte[], long> s_closeParameters = CreateCloseParameters();

    private static readonly long s_maxValidVariableLengthIntegerValue = (long)BigInteger.Pow(2, 62) - 1;
    private const long s_minValidVariableLengthIntegerValue = 0;

    private const string invalidUtf8String = "abc\uD801\uD802d";  // TODO: create test that this gets replaced by a fallback char
    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = new TheoryData<long> {
        s_minValidVariableLengthIntegerValue - 1,
        s_maxValidVariableLengthIntegerValue + 1,
    };

    private static TheoryData<byte[], long> CreateCloseParameters()
    {
        TheoryData<byte[], long> data = new();
        foreach (byte[] message in s_errorMessagesRaw)
        {
            foreach (long code in s_applicationErrorCodesRaw)
            {
                data.Add(message, code);
            }
        }
        return data;
    }


    private async Task AssertStreamIsClosedWithSpinWait(WebTransportStream stream)
    {
        try
        {
            await stream.WritesClosed;
        }
        catch (Exception) { }


        try
        {
            await stream.ReadsClosed;
        }
        catch (Exception) { }
    }

    [Theory]
    [MemberData(nameof(s_closeParameters))]
    public async Task SessionCloseAsyncSendsCorrectCapsule(byte[] expectedApplicationErrorMessage, long expectedApplicationErrorCode)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Memory<byte> errorCodeBuffer = new byte[4];
            await serverSession.ConnectStream.ReadExactlyAsync(errorCodeBuffer);
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await session.CloseAsync(expectedApplicationErrorCode, Encoding.UTF8.GetString(expectedApplicationErrorMessage));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public async Task SessionCloseAsyncThrowsOnInvalidParameters(long applicationErrorCode)
    {
        using Barrier barrier = new(2);
        var applicationErrorMessage = "valid message";

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.CloseAsync(applicationErrorCode, applicationErrorMessage));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_errorMessages))]
    public async Task ClientClosesSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Barrier barrier = new(2);
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_closeSessionCapsuleCode);
            Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, expectedApplicationErrorMessage.Length + applicationErrorCodeBuffer.Length);
            BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, expectedApplicationErrorCode);
            serverSession.ConnectStream.Write(applicationErrorCodeBuffer);
            serverSession.ConnectStream.Write(expectedApplicationErrorMessage);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ClosesClientSessionAfterServerClosesConnectStream()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();
            serverSession.ConnectStream.CompleteWrites();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ServerGracefullyClosesConnectStreamResultsInAllOtherStreamsBeingClosed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.CompleteWrites();

            barrier.SignalAndWait();
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(QuicAbortDirection.Write)]
    [InlineData(QuicAbortDirection.Read)]
    [InlineData(QuicAbortDirection.Both)]
    public async Task ServerAbortivelyClosesConnectStreamResultsInAllOtherStreamsBeingClosed(QuicAbortDirection abortDirection)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.Abort(abortDirection, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_errorMessages))]
    public async Task ClientClosesAllStreamsInSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Barrier barrier = new(2);
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

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

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionRequestCloseAsyncSendsCorrectCapsule()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_drainSessionCapsuleCode, capsuleCode);
            Assert.Equal(0, capsuleValueLength);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            await session.RequestCloseAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAndAllStreamsAfterReceivingDrainSessionCapsuleWhenDefaultHandlerIsUsed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHandlerAfterReceivingDrainSessionCapsule()
    {
        using Barrier barrier = new(2);
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 1);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                _webTransportServer.Address,
                _client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(); return Task.CompletedTask; } }
                );

            await wasHandlerCalledSemaphore.WaitAsync();

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, 0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReceiveDrainCapsuleWithInvalidValueClosesSession()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            int invalidLength = 1;
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(s_minValidSizeOfCloseSessionCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfCloseSessionCapsuleValue + 1)]
    public async Task ReceiveCloseSessionCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_drainSessionCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionAfterReceivingGoAwayFrameWhenDefaultHandlerIsUsed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            _ = serverSession.Connection.ShutdownAsync(); // don't await, because it requires the client to disconnect, which requires a closing WebTransport handshake

            Assert.Equal(-1, serverSession.ConnectStream.ReadByte()); // assert the reading side is closed

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [ConditionalFact]
    public async Task ClientCallsProvidedGracefulShutdownHandlerAfterReceivingGoAwayFrame()
    {
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 1);
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                _webTransportServer.Address,
                _client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(); return Task.CompletedTask; } }
                );

            barrier.SignalAndWait(); // Signal the session creation is completed

            await wasHandlerCalledSemaphore.WaitAsync(TestTimeoutInMilliseconds);

            barrier.SignalAndWait(); // Signal the handler was called
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            _ = serverSession.Connection.ShutdownAsync(); // don't await, it will complete only after the client closes the connection completely

            barrier.SignalAndWait(); // Wait for the handler to be called
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [ConditionalFact]
    public async Task ClientClosesSessionWhenQuicConnectionIsClosed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
            Assert.Null(session.CloseStatusCode);
            Assert.Null(session.CloseStatusDescription);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.DisposeAsync(); // This will close the underlying QUIC connection, which will result in the CONNECT stream being closed

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task AllOperationsThrowWebTransportExceptionOnClosedSession()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            await session.CloseAsync();

            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, TestTimeoutInMilliseconds);

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
            await Assert.ThrowsAsync<WebTransportException>(() => session.CloseAsync());

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}
