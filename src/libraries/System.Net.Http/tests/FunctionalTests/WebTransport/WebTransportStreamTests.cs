// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Quic;
using System.Threading.Tasks;
using Xunit;
using System.IO;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: add tests for when the client receives an invalid webtransport error code
// TODO: add tests for cancellations of stream operations
// TODO: test that CanRead returns false on a unidirectional stream
// TODO: test that CanWrite returns false on an accepted unidirectional stream
// TODO: write tests that complete writes is a noop on accepted streams
// TODO: write tests that complete writes is a noop on already closed streams
// TODO: add completewrites to list of all operations that should throw after stream is disposed
// TODO: write tests for writes with completeWrites: true
// TODO: write test that writes with compelteWrites: false does not complete the write side of the stream
// TODO: assert that normal write does not complete the write side of the stream
// TODO: test that ReadsClosed and WritesClosed complete when the respective side is closed
// TODO: test the behavior of WebTransportStream.DisposeAsync - sometimes it should abort and sometimes it should complete the stream cleanly
// TODO: write test that tries to read from a write-only stream and vice versa - both should throw invalidoperationexception
// TODO: write test that disposal of a stream that has unread data results in abort of read side and write side is always closed gracefully
// TODO: write a test that fails because we don't have RESET_STREAM_AT
// TODO: add completewrites and writeasync to list of all ops
// TODO: test that if we have a pending read operation and during that we dispose the stream, the read operation throws objectdisposedexception for the correct object
// TODO: add tests for default stream error code

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportStreamTests : WebTransportTestBase
{
    private const uint s_minValidErrorCode = 0;
    private const uint s_maxValidErrorCode = uint.MaxValue;

    private static readonly uint[] s_errorCodes = [s_minValidErrorCode, 10, int.MaxValue, s_maxValidErrorCode];
    private static readonly long[] s_invalidErrorCodes = [(long)s_minValidErrorCode - 1, (long)s_maxValidErrorCode + 1, long.MaxValue];

    public static readonly TheoryData<WebTransportStreamType, long> s_abortTestParameters = AbortTestParameters();
    public static readonly TheoryData<WebTransportStreamType, long> s_abortWithInvalidTestParameters = AbortWithInvalidErrorCodeTestParameters();

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

    public static readonly TheoryData<byte[]> s_dataToSend = [
        [1],
        [1, 2, 3]
    ];

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

    public WebTransportStreamTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ClientOpensStream(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ServerOpensStream(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Theory]
    [MemberData(nameof(s_dataToSendWithStreamType))]
    public async Task SendDataFromClientToServerOverClientInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);
            clientInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataFromServerToClientOverClientInitiatedStream(byte[] data)
    {
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);
            stream.Write(data);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await stream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataFromClientToServerOverServerInitiatedStream(byte[] data)
    {
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            serverInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSendWithStreamType))]
    public async Task SendDataFromServerToClientOverServerInitiatedStream(byte[] data, WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            serverInitiatedStream.Write(data);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>("abortDirection", () => serverInitiatedStream.Abort(invalidAbortDirection, 0));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(streamType);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
    public async Task ServerAbortsStreamWriteSideAbortsWithIncorrectErrorCode(WebTransportStreamType streamType)
    {
        using Barrier barrier = new Barrier(2);

        const long maxValidErrorCode = 0x52e5ac983162;
        const long invalidWebTransportErrorCode = maxValidErrorCode + 1;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            barrier.SignalAndWait();

            await AssertReadOperationsAndReadsClosedOnStreamThrowAsync<WebTransportException>(
                serverInitiatedStream,
                exceptionValidator: (ex) => Assert.Null(ex.ApplicationErrorCode)
                );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Write, invalidWebTransportErrorCode));
            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Read, invalidWebTransportErrorCode));
            Assert.Throws<ArgumentOutOfRangeException>(expectedParamName, () => serverInitiatedStream.Abort(WebTransportAbortDirection.Both, invalidWebTransportErrorCode));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task StreamCanBeDisposedMultipleTimes(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            await serverInitiatedStream.DisposeAsync();
            await serverInitiatedStream.DisposeAsync();

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task DisposedStreamTest(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            WebTransportStream serverInitiatedStream;
            using (serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType)) { }

            await AssertAllOperationsOnStreamThrowAsync<ObjectDisposedException>(serverInitiatedStream, exceptionValidator: null);
            Assert.False(serverInitiatedStream.CanRead);
            Assert.False(serverInitiatedStream.CanWrite);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            // TODO: assert that both sides are closed after disposal on the client side

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task NotSupportedOperationsThrows(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task WritesCompletedIsCompletedInAcceptedUnidirectionalStream()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            Assert.True(serverInitiatedStream.WritesClosed.IsCompletedSuccessfully);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReadsCompletedIsCompletedInUnidirectionalStream()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitatedStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            Assert.True(clientInitatedStream.ReadsClosed.IsCompletedSuccessfully);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitatedStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task CompleteWritesClosesWriteSide(WebTransportStreamType type)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            clientInitiatedStream.CompleteWrites();

            await clientInitiatedStream.WritesClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            Assert.Equal(-1, await clientInitatedStream.ReadByteAsync()); // reach end of stream -> ReadsClosed gets completed
            await clientInitatedStream.ReadsClosed;

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task WriteAsyncWithCompleteWritesTrueClosesWriteSide(WebTransportStreamType type)
    {
        using Barrier barrier = new(2);
        byte[] dataToSend = { 1, 2, 3 };

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            await clientInitiatedStream.WriteAsync(dataToSend, completeWrites: true);

            await clientInitiatedStream.WritesClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            byte[] receivedData = new byte[dataToSend.Length];
            await clientInitatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(dataToSend, receivedData);
            await clientInitatedStream.ReadsClosed;

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task WriteAsyncWithCompleteWritesFalseDoesNotCloseWriteSide(WebTransportStreamType type)
    {
        using Barrier barrier = new(2);
        byte[] dataToSend = { 1, 2, 3 };

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            await clientInitiatedStream.WriteAsync(dataToSend, completeWrites: false);

            Assert.False(clientInitiatedStream.WritesClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
            await using QuicStream clientInitatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            byte[] receivedData = new byte[dataToSend.Length];
            await clientInitatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(dataToSend, receivedData);

            Assert.False(clientInitatedStream.ReadsClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    private async Task AssertAllOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    private async Task AssertWriteOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
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

    private async Task AssertReadOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
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

    private async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    private async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    private async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    private async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }
}

