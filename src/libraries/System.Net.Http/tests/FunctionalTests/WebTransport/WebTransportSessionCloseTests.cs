// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using Xunit;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Net.Quic;
using System.IO;
using System.Collections.Generic;
using System.Net.Test.Common;
using System.Linq;

namespace System.Net.WebTransport.Functional.Tests;


[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionCloseTests : WebTransportTestBase
{
    private const int s_maxValidSizeOfCloseSessionCapsuleValue = 4 + 8192;
    private const int s_minValidSizeOfCloseSessionCapsuleValue = 4;

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

    private async Task AssertStreamAborted(QuicStream stream, QuicAbortDirection expectedAbortDirection, long expectedApplicationErrorCode)
    {
        if (expectedAbortDirection.HasFlag(QuicAbortDirection.Read))
        {
            QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await stream.ReadsClosed);
            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal(expectedApplicationErrorCode, ex.ApplicationErrorCode);
        }
        if (expectedAbortDirection.HasFlag(QuicAbortDirection.Write))
        {
            QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await stream.WritesClosed);
            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal(expectedApplicationErrorCode, ex.ApplicationErrorCode);
        }
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

    private async Task AssertStreamIsClosedWithSpinWait(WebTransportStream stream) => await AssertStreamIsClosedWithSpinWait(stream, null, null);
    private async Task AssertStreamIsClosedWithSpinWait(WebTransportStream stream, Action<WebTransportException> exceptionValidator) => await AssertStreamIsClosedWithSpinWait(stream, exceptionValidator, exceptionValidator);
    private async Task AssertStreamIsClosedWithSpinWait(WebTransportStream stream, Action<WebTransportException> writesClosedExceptionValidator, Action<WebTransportException> readsClosedExceptionValidator)
    {
        if (writesClosedExceptionValidator != null)
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => stream.WritesClosed);
            writesClosedExceptionValidator(ex);
        }
        else
        {
            await stream.WritesClosed;
        }

        if (readsClosedExceptionValidator != null)
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => stream.ReadsClosed);
            readsClosedExceptionValidator(ex);
        }
        else
        {
            await stream.ReadsClosed;
        }
    }

    [Theory]
    [MemberData(nameof(s_closeParameters))]
    public async Task SessionCloseAsyncSendsCorrectCapsuleAndClosesSession(byte[] expectedApplicationErrorMessage, long expectedApplicationErrorCode)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            var (receivedApplicationErrorCode, receivedErrorMessage) = await CapsuleHelper.ReadCloseSessionCapsule(serverSession.ConnectStream);

            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
            Assert.Equal(expectedApplicationErrorMessage, receivedErrorMessage);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);

            await session.CloseAsync(expectedApplicationErrorCode, Encoding.UTF8.GetString(expectedApplicationErrorMessage));

            Assert.Equal(WebTransportSessionState.ClosedLocally, session.State);
            await AssertStreamIsClosedWithSpinWait(stream, writesClosedExceptionValidator: ExceptionValidator, readsClosedExceptionValidator: null);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        void ExceptionValidator(WebTransportException ex)
        {
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);
            Assert.Null(ex.CloseStatusCode);
        }
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            var (receivedApplicationErrorCode, receivedApplicationErrorMessage) = await CapsuleHelper.ReadCloseSessionCapsule(serverSession.ConnectStream);

            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
            Assert.Equal(expectedApplicationErrorMessage, Encoding.UTF8.GetString(receivedApplicationErrorMessage.ToArray()));

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("closeStatus", async () => await session.CloseAsync(applicationErrorCode, applicationErrorMessage));

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            CapsuleHelper.WriteCloseSessionCapsule(serverSession.ConnectStream, expectedApplicationErrorMessage, expectedApplicationErrorCode);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            barrier.SignalAndWait();

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream, readsClosedExceptionValidator: ExceptionValidator, writesClosedExceptionValidator: null);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream, ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream, readsClosedExceptionValidator: null, writesClosedExceptionValidator: ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream, ExceptionValidator);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream inboundUnidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream inboundBidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            barrier.SignalAndWait();

            serverSession.ConnectStream.CompleteWrites();

            await AssertStreamAborted(outboundUnidirectionalStream, QuicAbortDirection.Write, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(outboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundUnidirectionalStream, QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        void ExceptionValidator(WebTransportException ex)
        {
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);
            Assert.Null(ex.CloseStatusCode);
        }
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedRemotely, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream, writesClosedExceptionValidator: null, readsClosedExceptionValidator: ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream, ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream, writesClosedExceptionValidator: ExceptionValidator, readsClosedExceptionValidator: null);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream, ExceptionValidator);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream inboundUnidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream inboundBidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            serverSession.ConnectStream.Abort(abortDirection, 0);

            await AssertStreamAborted(outboundUnidirectionalStream, QuicAbortDirection.Write, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(outboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundUnidirectionalStream, QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        void ExceptionValidator(WebTransportException ex)
        {
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);
            Assert.Null(ex.CloseStatusCode);
        }
        ;
    }

    [Theory]
    [MemberData(nameof(s_errorMessages))]
    public async Task ClientClosesAllStreamsInSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Barrier barrier = new(2);
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedRemotely, session.State);
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream, writesClosedExceptionValidator: null, readsClosedExceptionValidator: ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream, ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream, writesClosedExceptionValidator: ExceptionValidator, readsClosedExceptionValidator: null);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream, ExceptionValidator);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream inboundUnidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream inboundBidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            CapsuleHelper.WriteCloseSessionCapsule(serverSession.ConnectStream, expectedApplicationErrorMessage, expectedApplicationErrorCode);

            await AssertStreamAborted(outboundUnidirectionalStream, QuicAbortDirection.Write, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(outboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundUnidirectionalStream, QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        void ExceptionValidator(WebTransportException ex)
        {
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);
            Assert.Null(ex.CloseStatusCode);
        }
    }

    [Fact]
    public async Task SessionRequestCloseAsyncSendsCorrectCapsule()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await CapsuleHelper.ReadDrainCapsuleAsync(serverSession.ConnectStream);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await using WebTransportStream inboundUnidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream inboundBidirectionalStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional);
            await using WebTransportStream outboundUnidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            await using WebTransportStream outboundBidirectionalStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedLocally, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            await AssertStreamIsClosedWithSpinWait(inboundUnidirectionalStream, readsClosedExceptionValidator: ExceptionValidator, writesClosedExceptionValidator: null);
            await AssertStreamIsClosedWithSpinWait(inboundBidirectionalStream, ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundUnidirectionalStream, readsClosedExceptionValidator: null, writesClosedExceptionValidator: ExceptionValidator);
            await AssertStreamIsClosedWithSpinWait(outboundBidirectionalStream, ExceptionValidator);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await using QuicStream outboundUnidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream outboundBidirectionalStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Bidirectional);
            await using QuicStream inboundUnidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);
            await using QuicStream inboundBidirectionalStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            CapsuleHelper.WriteDrainCapsule(serverSession.ConnectStream);

            await AssertStreamAborted(outboundUnidirectionalStream, QuicAbortDirection.Write, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(outboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundUnidirectionalStream, QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
            await AssertStreamAborted(inboundBidirectionalStream, QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportSessionGone);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        void ExceptionValidator(WebTransportException ex)
        {
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);
            Assert.Null(ex.CloseStatusCode);
        }
    }

    [Fact]
    public async Task ClientCallsProvidedGracefulShutdownHandlerAfterReceivingDrainSessionCapsule()
    {
        using Barrier barrier = new(2);
        using SemaphoreSlim wasHandlerCalledSemaphore = new(0, 1);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(); return Task.CompletedTask; },
                    HttpVersion = HttpVersion.Version30,
                }
                );

            await wasHandlerCalledSemaphore.WaitAsync();

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            CapsuleHelper.WriteDrainCapsule(serverSession.ConnectStream);

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
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(2); return Task.CompletedTask; },
                    HttpVersion = HttpVersion.Version30,
                }
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            CapsuleHelper.WriteDrainCapsule(serverSession.ConnectStream);

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
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(2); return Task.CompletedTask; },
                    HttpVersion = HttpVersion.Version30,
                }
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, CapsuleHelper.s_drainSessionCapsuleCode);
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, CapsuleHelper.s_closeSessionCapsuleCode);
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.ClosedLocally, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await serverSession.Connection.ShutdownAsync(waitForClientDisconnectAndRejectNewStreams: false);

            // HACK: try-catch is necessary, because we don't have RESET_STREAM_AT support yet, so it's possible that the connection close or stream abort is faster than the frame with FIN flag
            try
            {
                Assert.Equal(-1, serverSession.ConnectStream.ReadByte()); // assert the reading side is closed
            } catch (QuicException) { }

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ClientClosesReceivedStreamForClosedSession(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession backgroundSession = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            barrier.SignalAndWait(); // Signal the session is closed

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession backgroundSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync(backgroundSession.Connection);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            CapsuleHelper.WriteDrainCapsule(serverSession.ConnectStream);

            barrier.SignalAndWait(); // Wait for the client to close the session

            await using QuicStream stream = await serverSession.OpenStreamFromServerAsync(streamType);

            QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await stream.WritesClosed);
            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal((long)Http3ErrorCode.WebtransportSessionGone, ex.ApplicationErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ClientClosesPendingStreamsWhenSessionIsClosedByServer(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            barrier.SignalAndWait(); // Signal the session creation is completed

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            // To make sure the pending streams are already processed by the client, we open more than allowed and wait for one to be rejected.
            // This is a deterministic way to ensure that at least one stream is pending in the client's channel.

            (List<QuicStream> pendingStreams, QuicStream rejectedStream) = await WebTransportSessionTestHelper.OpenMorePendingStreamsThanAllowed(serverSession, streamType);

            QuicException writesClosedQex = await Assert.ThrowsAsync<QuicException>(async () => await rejectedStream.WritesClosed);
            Assert.Equal(QuicError.StreamAborted, writesClosedQex.QuicError);
            Assert.Equal((long)Http3ErrorCode.WebTransportBufferedStreamRejected, writesClosedQex.ApplicationErrorCode);

            // Now we are sure that the pending streams are in the channel

            serverSession.ConnectStream.Abort(QuicAbortDirection.Both, 0);

            foreach (QuicStream stream in pendingStreams)
            {
                if (stream == rejectedStream)
                {
                    continue;
                }

                QuicException writesClosedEx = await Assert.ThrowsAsync<QuicException>(async () => await stream.WritesClosed);
                Assert.Equal(QuicError.StreamAborted, writesClosedEx.QuicError);
                Assert.Equal((long)Http3ErrorCode.WebtransportSessionGone, writesClosedEx.ApplicationErrorCode);

                if (streamType == WebTransportStreamType.Bidirectional)
                {
                    QuicException readsClosedEx = await Assert.ThrowsAsync<QuicException>(async () => await stream.ReadsClosed);
                    Assert.Equal(QuicError.StreamAborted, readsClosedEx.QuicError);
                    Assert.Equal((long)Http3ErrorCode.WebtransportSessionGone, readsClosedEx.ApplicationErrorCode);
                }

                await stream.DisposeAsync();
            }

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionIsClosedIfDefaultShutdownHandlerThrowsWhenGoawayIsReceived()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession backgroundSession = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => Task.CompletedTask,
                    HttpVersion = HttpVersion.Version30,
                });
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { throw new Exception(); },
                    HttpVersion = HttpVersion.Version30,
                }
                );

            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession backgroundSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync(backgroundSession.Connection);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await using QuicStream establishedStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            // To make sure the pending streams are already processed by the client, we open more than allowed and wait for one to be rejected.
            // This is a deterministic way to ensure that at least one stream is pending in the client's channel.
            (List<QuicStream> openStreams, QuicStream rejectedStream) = await WebTransportSessionTestHelper.OpenMorePendingStreamsThanAllowed(serverSession, WebTransportStreamType.Bidirectional);

            QuicStream pendingStream = openStreams.First(stream => !stream.WritesClosed.IsCompleted); // Get one pending stream and assert it will be closed after the session is closed

            await serverSession.Connection.ShutdownAsync(waitForClientDisconnectAndRejectNewStreams: false);

            List<QuicException> exceptions = new();
            exceptions.Add(Assert.Throws<QuicException>(() => serverSession.ConnectStream.ReadByte()));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => establishedStream.ReadsClosed));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => establishedStream.WritesClosed));

            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => pendingStream.ReadsClosed));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => pendingStream.WritesClosed));


            foreach (QuicException ex in exceptions)
            {
                Assert.Equal(QuicError.StreamAborted, ex.QuicError);
                Assert.Equal((long)Http3ErrorCode.WebtransportSessionGone, ex.ApplicationErrorCode);
            }

            await Task.WhenAll(openStreams.Select(stream => stream.DisposeAsync().AsTask()).ToArray());

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionIsClosedIfServerClosesQuicConnection()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => Task.CompletedTask,
                    HttpVersion = HttpVersion.Version30,
                });

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedRemotely, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession session = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await session.Connection.CloseAsync(0);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionIsClosedIfDefaultShutdownHandlerThrowsWhenDrainIsReceived()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession backgroundSession = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => Task.CompletedTask,
                    HttpVersion = HttpVersion.Version30,
                });
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { throw new Exception(); },
                    HttpVersion = HttpVersion.Version30,
                }
                );

            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional);

            barrier.SignalAndWait(); // Signal the session creation is completed

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);
            Assert.Null(session.CloseStatusDescription);
            Assert.Null(session.CloseStatusCode);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession backgroundSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync(backgroundSession.Connection);

            barrier.SignalAndWait(); // Wait for the client to complete session creation

            await using QuicStream establishedStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Bidirectional);

            // To make sure the pending streams are already processed by the client, we open more than allowed and wait for one to be rejected.
            // This is a deterministic way to ensure that at least one stream is pending in the client's channel.
            (List<QuicStream> openStreams, QuicStream rejectedStream) = await WebTransportSessionTestHelper.OpenMorePendingStreamsThanAllowed(serverSession, WebTransportStreamType.Bidirectional);

            QuicStream pendingStream = openStreams.First(stream => !stream.WritesClosed.IsCompleted); // Get one pending stream and assert it will be closed after the session is closed

            CapsuleHelper.WriteDrainCapsule(serverSession.ConnectStream);

            List<QuicException> exceptions = new();
            exceptions.Add(Assert.Throws<QuicException>(() => serverSession.ConnectStream.ReadByte()));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => establishedStream.ReadsClosed));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => establishedStream.WritesClosed));

            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => pendingStream.ReadsClosed));
            exceptions.Add(await Assert.ThrowsAsync<QuicException>(() => pendingStream.WritesClosed));


            foreach (QuicException ex in exceptions)
            {
                Assert.Equal(QuicError.StreamAborted, ex.QuicError);
                Assert.Equal((long)Http3ErrorCode.WebtransportSessionGone, ex.ApplicationErrorCode);
            }

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
                new WebTransportSessionCreationOptions
                {
                    Uri = _webTransportServer.Address,
                    HttpMessageInvoker = _client,
                    DefaultStreamErrorCode = 0,
                    GracefulShutdownHandler = (_) => { wasHandlerCalledSemaphore.Release(); return Task.CompletedTask; },
                    HttpVersion = HttpVersion.Version30,
                }
                );

            barrier.SignalAndWait(); // Signal the session creation is completed

            await wasHandlerCalledSemaphore.WaitAsync(TestTimeoutInMilliseconds);

            barrier.SignalAndWait(); // Signal the handler was called
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await session.CloseAsync();

            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            await WebTransportSessionTestHelper.AssertAllOperationsOnSessionThrowAsync<WebTransportException>(session, (ex) => Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            ValueTask<WebTransportStream> acceptStreamTask = session.AcceptInboundStreamAsync(streamType);

            await session.CloseAsync();

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await acceptStreamTask);
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionCanBeClosedOrRequestedToBeClosedWhenItIsAlreadyClosed()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await session.CloseAsync();

            // The following calls should not throw
            await session.CloseAsync();
            await session.CloseAsync(0, "message");
            await session.RequestCloseAsync();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

}
