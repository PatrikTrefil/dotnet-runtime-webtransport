// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Net.Quic;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class WebTransportStreamAbortTests : WebTransportTestBase
{
    private const uint s_minValidErrorCode = 0;
    private const uint s_maxValidErrorCode = uint.MaxValue;

    private static readonly uint[] s_errorCodes = [s_minValidErrorCode, 10, int.MaxValue, s_maxValidErrorCode];
    private static readonly long[] s_invalidErrorCodes = [(long)s_minValidErrorCode - 1, (long)s_maxValidErrorCode + 1, long.MaxValue];

    public static readonly TheoryData<WebTransportStreamType, long> s_abortTestParameters = AbortTestParameters();
    public static readonly TheoryData<WebTransportStreamType, long> s_abortWithInvalidTestParameters = AbortWithInvalidErrorCodeTestParameters();

    public static readonly TheoryData<WebTransportStreamType, WebTransportAbortDirection> s_multipleAbortTestParameters = MultipleAbortTestParameters();

    private static TheoryData<WebTransportStreamType, WebTransportAbortDirection> MultipleAbortTestParameters()
    {
        var theoryData = new TheoryData<WebTransportStreamType, WebTransportAbortDirection>();
        foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (WebTransportAbortDirection abortDirection in Enum.GetValues(typeof(WebTransportAbortDirection)))
            {
                theoryData.Add(streamType, abortDirection);
            }
        }
        return theoryData;
    }

    private static TheoryData<WebTransportStreamType, long> AbortTestParameters()
    {
        var theoryData = new TheoryData<WebTransportStreamType, long>();
        foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (uint errorCode in s_errorCodes)
            {
                theoryData.Add(streamType, errorCode);
            }
        }
        return theoryData;
    }

    private static TheoryData<WebTransportStreamType, long> AbortWithInvalidErrorCodeTestParameters()
    {
        var theoryData = new TheoryData<WebTransportStreamType, long>();
        foreach (WebTransportStreamType streamType in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (long errorCode in s_invalidErrorCodes)
            {
                theoryData.Add(streamType, errorCode);
            }
        }
        return theoryData;
    }

    // TODO: uncomment this after RESET_STREAM_AT is added to System.Net.Quic
    //[Theory]
    //[MemberData(nameof(s_dataToSendWithStreamType))]
    private async Task DataIsNotLostWhenStreamIsAborted(byte[] data, WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            byte[] receivedData = new byte[data.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);
            clientInitiatedStream.Write(data);
            clientInitiatedStream.Abort(WebTransportAbortDirection.Both, 0);

            barrier.SignalAndWait();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]

    public async Task AbortStreamWithInvalidAbortDirectionThrows(WebTransportStreamType streamType)
    {
        var invalidAbortDirection = (WebTransportAbortDirection)42;

        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>("abortDirection", () => serverInitiatedStream.Abort(invalidAbortDirection, 0));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ClientAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Barrier barrier = new Barrier(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            await WebTransportStreamTestHelper.AssertReadOperationsAndReadsClosedOnStreamThrowAsync<QuicException>(
                clientInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ErrorCodeRemapping.HttpCodeToWebTransportCode((long)ex.ApplicationErrorCode))
                );

            Assert.True(clientInitiatedStream.ReadsClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(WebTransportAbortDirection.Write, expectedWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Theory]
    [MemberData(nameof(s_multipleAbortTestParameters))]
    public async Task ClientCanAbortStreamMultipleTimes(WebTransportStreamType streamType, WebTransportAbortDirection abortDirection)
    {
        using Barrier barrier = new Barrier(2);
        const int expectedWebTransportErrorCode = 42;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(abortDirection, expectedWebTransportErrorCode);
            clientInitiatedStream.Abort(abortDirection, expectedWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ClientAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Barrier barrier = new Barrier(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            await WebTransportStreamTestHelper.AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<QuicException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Equal(expectedWebTransportErrorCode, ErrorCodeRemapping.HttpCodeToWebTransportCode((long)ex.ApplicationErrorCode))
                );

            Assert.True(serverInitiatedStream.WritesClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(WebTransportAbortDirection.Read, expectedWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ServerAbortsStreamReadSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Barrier barrier = new Barrier(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await WebTransportStreamTestHelper.AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<WebTransportException>(
                clientInitiatedStream,
                exceptionValidator: (ex) =>
                {
                    Assert.Equal(WebTransportError.StreamAborted, ex.WebTransportError);
                    Assert.Equal(expectedWebTransportErrorCode, ex.CloseStatusCode);
                }
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            clientInitiatedStream.Abort(QuicAbortDirection.Read, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_abortTestParameters))]
    public async Task ServerAbortsStreamWriteSideAbortsWithCorrectErrorCode(WebTransportStreamType streamType, long expectedWebTransportErrorCode)
    {
        using Barrier barrier = new Barrier(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await WebTransportStreamTestHelper.AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportException>(
                serverInitiatedStream,
                exceptionValidator: (ex) =>
                {
                    Assert.Equal(WebTransportError.StreamAborted, ex.WebTransportError);
                    Assert.Equal(expectedWebTransportErrorCode, ex.CloseStatusCode);
                }
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(QuicAbortDirection.Write, ErrorCodeRemapping.WebTransportCodeToHttpCode(expectedWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ServerAbortsStreamWriteSideAbortsWithInvalidErrorCode(WebTransportStreamType streamType)
    {
        using Barrier barrier = new Barrier(2);

        const long maxValidErrorCode = 0x52e5ac983162;
        const long invalidWebTransportErrorCode = maxValidErrorCode + 1;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await WebTransportStreamTestHelper.AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Null(ex.CloseStatusCode)
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            serverInitiatedStream.Abort(QuicAbortDirection.Write, invalidWebTransportErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_abortWithInvalidTestParameters))]
    public async Task ClientAbortsStreamWithInvalidErrorCode(WebTransportStreamType streamType, long invalidWebTransportErrorCode)
    {
        using Barrier barrier = new Barrier(2);

        string expectedParamName = "errorCode";

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Write, invalidWebTransportErrorCode));
            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Read, invalidWebTransportErrorCode));
            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Both, invalidWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

}
