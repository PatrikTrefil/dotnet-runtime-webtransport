// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using Xunit;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace System.Net.WebTransport.Functional.Tests;


[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportStreamTests : WebTransportTestBase
{
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

    private static readonly TheoryData<Func<WebTransportStream, CancellationToken, Task>> s_writeOperationsAsParameters = [
        (stream, cancellationToken) => stream.WriteAsync(new byte[1], cancellationToken).AsTask(),
        ];
    private static readonly TheoryData<Func<WebTransportStream, CancellationToken, Task>> s_readOperationsAsParameters = [
        (stream, sancellationToken) => stream.ReadAsync(new byte[1], sancellationToken).AsTask(),
        (stream, cancellationToken) => stream.ReadAtLeastAsync(new byte[1], 1, cancellationToken: cancellationToken).AsTask(),
        (stream, cancellationToken) => stream.ReadExactlyAsync(new byte[1], cancellationToken: cancellationToken).AsTask(),
        (stream, cancellationToken) => stream.CopyToAsync(new MemoryStream(), 10, cancellationToken),
        ];

    public static readonly TheoryData<WebTransportStreamType, Func<WebTransportStream, CancellationToken, Task>, QuicAbortDirection> s_operationCanceledTestParameters = OperationCanceledTestParameters();

    private static TheoryData<WebTransportStreamType, Func<WebTransportStream, CancellationToken, Task>, QuicAbortDirection> OperationCanceledTestParameters()
    {
        TheoryData<WebTransportStreamType, Func<WebTransportStream, CancellationToken, Task>, QuicAbortDirection> theoryData = new();

        foreach (WebTransportStreamType type in Enum.GetValues(typeof(WebTransportStreamType)))
        {
            foreach (Func<WebTransportStream, CancellationToken, Task> operation in s_writeOperationsAsParameters)
            {
                theoryData.Add(type, operation, QuicAbortDirection.Write);
            }
        }

        foreach (Func<WebTransportStream, CancellationToken, Task> operation in s_readOperationsAsParameters)
        {
            theoryData.Add(WebTransportStreamType.Bidirectional, operation, QuicAbortDirection.Read);
        }

        return theoryData;
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ClientOpensStream(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task CanWriteAndCanReadReturnCorrectValues(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

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

            WebTransportStream stream;
            await using (stream = await session.OpenOutboundStreamAsync(streamType))
            {
                if (streamType == WebTransportStreamType.Bidirectional)
                {
                    Assert.True(stream.CanRead);
                    Assert.True(stream.CanWrite);
                }
                else
                {
                    Assert.False(stream.CanRead);
                    Assert.True(stream.CanWrite);
                }

                barrier.SignalAndWait();
            }

            Assert.False(stream.CanRead);
            Assert.False(stream.CanWrite);


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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(streamType);
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

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSendWithStreamType))]
    public async Task DataIsNotLostWhenStreamIsClosedGracefully(byte[] data, WebTransportStreamType streamType)
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

            await clientInitiatedStream.WriteAsync(data, completeWrites: true);

            barrier.SignalAndWait();

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);
            stream.Write(data);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            serverInitiatedStream.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataRepeatedlyDoesNotThrow(byte[] data)
    {
        using Barrier barrier = new(2);
        const int iterations = 5;
        byte[] expectedData = Enumerable.Repeat(data, iterations).SelectMany(d => d).ToArray();

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            List<byte> receivedData = new();

            byte[] buffer = new byte[10];
            int bytesRead;
            while ((bytesRead = await serverInitiatedStream.ReadAsync(buffer)) > 0)
            {
                receivedData.AddRange(buffer.AsSpan(0, bytesRead));
            }

            Assert.Equal(expectedData, receivedData.ToArray());

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            for (int i = 0; i < iterations; i++)
            {
                await serverInitiatedStream.WriteAsync(data);
            }

            await serverInitiatedStream.WriteAsync(ReadOnlyMemory<byte>.Empty, completeWrites: true);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_dataToSend))]
    public async Task SendDataOverMultipleSessionsAndStreams(byte[] data)
    {
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Bidirectional; // only makes sense for bidirectional
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession1 = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using WebTransportServerSession serverSession2 = await _webTransportServer.AcceptWebTransportServerSessionAsync(serverSession1.Connection);


            await using QuicStream serverInitiatedStreamSession1 = await serverSession1.OpenStreamFromServerAsync(streamType);
            await ReceiveData(serverInitiatedStreamSession1);

            await using QuicStream serverInitiatedStreamSession2 = await serverSession2.OpenStreamFromServerAsync(streamType);
            await ReceiveData(serverInitiatedStreamSession2);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session1 = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await using WebTransportSession session2 = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);

            await using WebTransportStream serverInitiatedStreamSession1 = await session1.AcceptInboundStreamAsync(streamType);
            serverInitiatedStreamSession1.Write(data);

            await using WebTransportStream serverInitiatedStreamSession2 = await session2.AcceptInboundStreamAsync(streamType);
            serverInitiatedStreamSession2.Write(data);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);

        async Task ReceiveData(QuicStream stream)
        {
            byte[] receivedData = new byte[data.Length];
            await stream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);
        }
    }

    // TODO: uncomment and make this test public once QUIC fixes the underlying issue
    //[Theory]
    //[InlineData(WebTransportStreamType.Unidirectional)]
    //[InlineData(WebTransportStreamType.Bidirectional)]
    private async Task WriteAfterCompleteWritesThrows(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);

            stream.CompleteWrites();

            await WebTransportStreamTestHelper.AssertWriteOperationsOnStreamThrowAsync<WebTransportException>(
                stream,
                (ex) => Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError)
            );

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task ReadsAfterEndOfStreamIsReachedThrow(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.AcceptInboundStreamAsync(streamType);

            await Assert.ThrowsAsync<EndOfStreamException>(() => stream.ReadExactlyAsync(new byte[1]).AsTask());
            await Assert.ThrowsAsync<EndOfStreamException>(() => stream.ReadAtLeastAsync(new byte[2], 1).AsTask());
            Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(new byte[1]));
            Assert.Throws<EndOfStreamException>(() => stream.ReadAtLeast(new byte[2], 1));
            Assert.Equal(-1, stream.ReadByte());

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.OpenStreamFromServerAsync(streamType);

            stream.CompleteWrites();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task CompleteWritesDoesNotThrowsOnReceivedUnidirectionalStream()
    {
        using Barrier barrier = new(2);
        WebTransportStreamType streamType = WebTransportStreamType.Unidirectional;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.AcceptInboundStreamAsync(streamType);

            stream.CompleteWrites(); // writes are already completed, so this should be a no-op

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task WriteOperationsThrowOnReceivedUnidirectionalStream()
    {
        using Barrier barrier = new(2);
        WebTransportStreamType streamType = WebTransportStreamType.Unidirectional;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.AcceptInboundStreamAsync(streamType);

            await WebTransportStreamTestHelper.AssertWriteOperationsOnStreamThrowAsync<InvalidOperationException>(stream, null);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.OpenStreamFromServerAsync(streamType);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReadOperationsThrowOnClientInitiatedUnidirectionalStream()
    {
        using Barrier barrier = new(2);
        WebTransportStreamType streamType = WebTransportStreamType.Unidirectional;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);

            await WebTransportStreamTestHelper.AssertReadOperationsOnStreamThrowAnyAsync<InvalidOperationException>(stream, null);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            byte[] receivedData = new byte[data.Length];
            await serverInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(data, receivedData);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);
            serverInitiatedStream.Write(data);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            await serverInitiatedStream.DisposeAsync();
            await serverInitiatedStream.DisposeAsync();

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
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task DisposedAsyncStreamTest(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);
        long defaultErrorCode = 42;
        long remappedErrorCode = ErrorCodeRemapping.WebTransportCodeToHttpCode(defaultErrorCode);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = defaultErrorCode,
                HttpVersion = HttpVersion.Version30
            });

            WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            Task readExactlyTask = serverInitiatedStream.ReadExactlyAsync(new byte[3]).AsTask();
            await serverInitiatedStream.DisposeAsync();

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => readExactlyTask);
            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);

            await WebTransportStreamTestHelper.AssertAllOperationsOnStreamThrowAsync<ObjectDisposedException>(serverInitiatedStream, exceptionValidator: null);
            Assert.False(serverInitiatedStream.CanRead);
            Assert.False(serverInitiatedStream.CanWrite);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            if (streamType == WebTransportStreamType.Bidirectional)
            {
                Assert.Equal(-1, serverInitiatedStream.ReadByte());
                await serverInitiatedStream.ReadsClosed;
            }


            QuicException ex = await Assert.ThrowsAsync<QuicException>(() => serverInitiatedStream.WritesClosed);

            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal(remappedErrorCode, (long)ex.ApplicationErrorCode);

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
        long defaultErrorCode = 42;
        long remappedErrorCode = ErrorCodeRemapping.WebTransportCodeToHttpCode(defaultErrorCode);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = defaultErrorCode,
                HttpVersion = HttpVersion.Version30
            });

            WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);
            serverInitiatedStream.Dispose();

            await WebTransportStreamTestHelper.AssertAllOperationsOnStreamThrowAsync<ObjectDisposedException>(serverInitiatedStream, exceptionValidator: null);
            Assert.False(serverInitiatedStream.CanRead);
            Assert.False(serverInitiatedStream.CanWrite);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream serverInitiatedStream = await serverSession.OpenStreamFromServerAsync(streamType);

            if (streamType == WebTransportStreamType.Bidirectional)
            {
                Assert.Equal(-1, serverInitiatedStream.ReadByte());
                await serverInitiatedStream.ReadsClosed;
            }

            QuicException ex = await Assert.ThrowsAsync<QuicException>(() => serverInitiatedStream.WritesClosed);

            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal(remappedErrorCode, (long)ex.ApplicationErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task InvalidOperationsThrow(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(streamType);

            Assert.Throws<NotSupportedException>(() => serverInitiatedStream.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => serverInitiatedStream.SetLength(10));
            Assert.Throws<NotSupportedException>(() => { long position = serverInitiatedStream.Position; });
            Assert.Throws<NotSupportedException>(() => { long length = serverInitiatedStream.Length; });
            Assert.Throws<NotSupportedException>(() => { serverInitiatedStream.Position = 1; });

            if (streamType == WebTransportStreamType.Unidirectional)
            {
                await WebTransportStreamTestHelper.AssertWriteOperationsOnStreamThrowAsync<InvalidOperationException>(serverInitiatedStream, exceptionValidator: null);
            }

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

    [Fact]
    public async Task WritesCompletedIsCompletedInAcceptedUnidirectionalStream()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream serverInitiatedStream = await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional);
            Assert.True(serverInitiatedStream.WritesClosed.IsCompletedSuccessfully);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional);
            Assert.True(clientInitiatedStream.ReadsClosed.IsCompletedSuccessfully);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(WebTransportStreamType.Unidirectional);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            clientInitiatedStream.CompleteWrites();

            await clientInitiatedStream.WritesClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            Assert.Equal(-1, await clientInitiatedStream.ReadByteAsync()); // reach end of stream -> ReadsClosed gets completed
            await clientInitiatedStream.ReadsClosed;

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task CompleteWritesOnClosedStreamDoesNotThrow(WebTransportStreamType type)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            clientInitiatedStream.CompleteWrites();

            await clientInitiatedStream.WritesClosed;

            clientInitiatedStream.CompleteWrites();

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(type);

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            await clientInitiatedStream.WriteAsync(dataToSend, completeWrites: true);

            await clientInitiatedStream.WritesClosed;

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            byte[] receivedData = new byte[dataToSend.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(dataToSend, receivedData);
            await clientInitiatedStream.ReadsClosed;

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
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_defaultWebTransportSessionCreationOptions);
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            await clientInitiatedStream.WriteAsync(dataToSend, completeWrites: false);

            Assert.False(clientInitiatedStream.WritesClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            byte[] receivedData = new byte[dataToSend.Length];
            await clientInitiatedStream.ReadExactlyAsync(receivedData);
            Assert.Equal(dataToSend, receivedData);

            Assert.False(clientInitiatedStream.ReadsClosed.IsCompleted);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_operationCanceledTestParameters))]
    public async Task OperationCanceledExceptionIsThrownWhenCancellationIsRequested(WebTransportStreamType type, Func<WebTransportStream, CancellationToken, Task> operation, QuicAbortDirection expectedClientAbortDirection)
    {
        using Barrier barrier = new(2);
        const long defaultStreamErrorCode = 42;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = defaultStreamErrorCode,
                HttpVersion = HttpVersion.Version30,
            });
            await using WebTransportStream clientInitiatedStream = await session.OpenOutboundStreamAsync(type);

            barrier.SignalAndWait();

            CancellationTokenSource cts = new();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(clientInitiatedStream, cts.Token));

            Task sideExpectedToBeClosed;
            Task? sideExpectedToBeOpen;

            switch (expectedClientAbortDirection)
            {
                case QuicAbortDirection.Read:
                    sideExpectedToBeClosed = clientInitiatedStream.ReadsClosed;
                    sideExpectedToBeOpen = clientInitiatedStream.WritesClosed;
                    break;
                case QuicAbortDirection.Write:
                    sideExpectedToBeClosed = clientInitiatedStream.WritesClosed;
                    if (type == WebTransportStreamType.Bidirectional)
                    {
                        sideExpectedToBeOpen = clientInitiatedStream.ReadsClosed;
                    }
                    else
                    {
                        sideExpectedToBeOpen = null;
                    }
                    break;
                default:
                    throw new ArgumentException("Invalid abort direction", nameof(expectedClientAbortDirection));
            }

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await sideExpectedToBeClosed);

            Assert.Equal(WebTransportError.OperationAborted, ex.WebTransportError);

            if (sideExpectedToBeOpen != null)
            {
                Assert.False(sideExpectedToBeOpen.IsCompleted);
            }

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using QuicStream clientInitiatedStream = await serverSession.AcceptStreamFromServerAsync(type);

            barrier.SignalAndWait();

            QuicException ex = await Assert.ThrowsAsync<QuicException>(() =>
            {
                switch (expectedClientAbortDirection)
                {
                    case QuicAbortDirection.Read:
                        return clientInitiatedStream.WritesClosed;
                    case QuicAbortDirection.Write:
                        return clientInitiatedStream.ReadsClosed;
                    default:
                        throw new ArgumentException("Invalid abort direction", nameof(expectedClientAbortDirection));
                }
            });

            Assert.Equal(QuicError.StreamAborted, ex.QuicError);
            Assert.Equal(ErrorCodeRemapping.WebTransportCodeToHttpCode(defaultStreamErrorCode), (long)ex.ApplicationErrorCode);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}

