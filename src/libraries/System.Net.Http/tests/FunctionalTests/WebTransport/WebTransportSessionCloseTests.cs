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
using System.IO;
using System.Net.Test.Common;

namespace System.Net.WebTransport.Functional.Tests;

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

    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = new TheoryData<long> {
        VariableLengthIntegerHelper.MinValue - 1,
        VariableLengthIntegerHelper.MaxValue + 1,
    };

    private static void WriteDrainCapsule(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_drainSessionCapsuleCode);
        VariableLengthIntegerStreamHelper.Write(stream, 0);
    }

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

            Memory<byte> messageBuffer = new byte[capsuleValueLength - sizeof(uint)];
            await serverSession.ConnectStream.ReadExactlyAsync(messageBuffer);

            Assert.Equal(s_closeSessionCapsuleCode, capsuleCode);
            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
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

    [Fact]
    public async Task SessionCloseAsyncWithNonAsciiCharacterGetsCorrentlyEncoded()
    {
        using Barrier barrier = new(2);
        const string invalidUtf8String = "abc\uD801\uD802d";
        const string expectedApplicationErrorMessage = "abc��d";
        const long expectedApplicationErrorCode = 1;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Memory<byte> errorCodeBuffer = new byte[4];
            await serverSession.ConnectStream.ReadExactlyAsync(errorCodeBuffer);
            uint receivedApplicationErrorCode = BinaryPrimitives.ReadUInt32BigEndian(errorCodeBuffer.Span);

            Memory<byte> messageBuffer = new byte[capsuleValueLength - sizeof(uint)];
            await serverSession.ConnectStream.ReadExactlyAsync(messageBuffer);

            Assert.Equal(s_closeSessionCapsuleCode, capsuleCode);
            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
            Assert.Equal(sizeof(uint) + messageBuffer.Length, capsuleValueLength);
            Assert.Equal(expectedApplicationErrorMessage, Encoding.UTF8.GetString(messageBuffer.ToArray()));

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await session.CloseAsync(expectedApplicationErrorCode, invalidUtf8String);

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

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
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

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
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
    public async Task ServerGracefullyClosesConnectStreamWriteSideResultsInAllOtherStreamsBeingClosed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
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

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.CompleteWrites();

            barrier.SignalAndWait();
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(QuicAbortDirection.Write)]
    [InlineData(QuicAbortDirection.Both)]
    [InlineData(QuicAbortDirection.Read)]
    public async Task ServerAbortivelyClosesConnectStreamResultsInAllOtherStreamsBeingClosed(QuicAbortDirection abortDirection)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedRemotely, session.State);
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

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

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
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
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

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

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

    [Fact]
    public async Task ClientClosesSessionAndAllStreamsAfterReceivingDrainSessionCapsuleWhenDefaultHandlerIsUsed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedLocally, session.State);
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

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream unidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream bidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            WriteDrainCapsule(serverSession.ConnectStream);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
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

            WriteDrainCapsule(serverSession.ConnectStream);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionIsUsableAfterDrainCapsuleIsReceivedAndProcessed()
    {
        using Barrier barrier = new(2);
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 2);

        byte expectedByte = 10;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                _webTransportServer.Address,
                _client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(2); return Task.CompletedTask; } }
                );

            await wasHandlerCalledSemaphore.WaitAsync();

            await using WebTransportStream outboundStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);

            outboundStream.WriteByte(expectedByte);

            await using WebTransportStream inboundStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);

            int receivedByte = await inboundStream.ReadByteAsync();

            Assert.Equal(expectedByte, receivedByte);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            WriteDrainCapsule(serverSession.ConnectStream);

            await wasHandlerCalledSemaphore.WaitAsync();

            await using QuicStream inboundStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            int receivedByteInboudStream = await inboundStream.ReadByteAsync();

            await using QuicStream outboundStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            outboundStream.WriteByte(expectedByte);

            Assert.Equal(expectedByte, receivedByteInboudStream);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    private async Task SessionIsUsableAfterGoawayIsReceivedAndProcessed()
    {
        using Barrier barrier = new(2);
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 2);

        byte expectedByte = 10;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                _webTransportServer.Address,
                _client,
                new WebTransportSessionCreationOptions { GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(2); return Task.CompletedTask; } }
                );

            barrier.SignalAndWait();

            await wasHandlerCalledSemaphore.WaitAsync();

            await using WebTransportStream outboundStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);

            outboundStream.WriteByte(expectedByte);

            // Opening two streams because Http3Connection checks for connection shutdown after every stream accept
            await using WebTransportStream inboundStream1 = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);

            int receivedByte1 = await inboundStream1.ReadByteAsync();

            await using WebTransportStream inboundStream2 = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);

            int receivedByte2 = await inboundStream2.ReadByteAsync();

            Assert.Equal(expectedByte, receivedByte1);
            Assert.Equal(expectedByte, receivedByte2);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait();

            await serverSession.Connection.ShutdownAsync(waitForClientDisconnectAndRejectNewStreams: false);

            await wasHandlerCalledSemaphore.WaitAsync();

            await using QuicStream inboundStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            int receivedByteInboudStream = await inboundStream.ReadByteAsync();

            await using QuicStream outboundStream1 = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            outboundStream1.WriteByte(expectedByte);

            await using QuicStream outboundStream2 = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            outboundStream2.WriteByte(expectedByte);

            Assert.Equal(expectedByte, receivedByteInboudStream);

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
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

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
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

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

    [Fact]
    public async Task ClientClosesSessionAfterReceivingGoAwayFrameWhenDefaultHandlerIsUsed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedLocally, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.Connection.ShutdownAsync(waitForClientDisconnectAndRejectNewStreams: false);

            Assert.Equal(-1, serverSession.ConnectStream.ReadByte()); // assert the reading side is closed

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
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

            await serverSession.Connection.ShutdownAsync(waitForClientDisconnectAndRejectNewStreams: false);

            barrier.SignalAndWait(); // Wait for the handler to be called
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ClientClosesSessionWhenQuicConnectionIsClosed()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedRemotely, session.State);
            Assert.Null(session.CloseStatusCode);
            Assert.Null(session.CloseStatusDescription);

            await WebTransportSessionTestHelper.AssertAllOperationsOnSessionThrowAsync<WebTransportException>(session, (ex) => Assert.Equal(WebTransportError.SessionClosedByPeer, ex.WebTransportError));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.Connection.CloseAsync(0); // This will close the underlying QUIC connection, which will result in the CONNECT stream being closed

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ClientClosesSessionWhenConnectStreamIsAborted()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedRemotely, session.State);
            Assert.Null(session.CloseStatusCode);
            Assert.Null(session.CloseStatusDescription);

            await WebTransportSessionTestHelper.AssertAllOperationsOnSessionThrowAsync<WebTransportException>(session, (ex) => Assert.Equal(WebTransportError.SessionClosedByPeer, ex.WebTransportError));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            serverSession.ConnectStream.Abort(QuicAbortDirection.Both, 0);

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

            session.Close();

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            await WebTransportSessionTestHelper.AssertAllOperationsOnSessionThrowAsync<WebTransportException>(session, (ex) => Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ThrowsWhenSessionIsClosedDuringAcceptInboundStream(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            ValueTask<WebTransportStream> acceptStreamTask = session.AcceptInboundStreamAsync(streamType);

            session.Close();

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await acceptStreamTask);
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);

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
