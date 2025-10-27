// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Test.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;
using System.Net.Http;
using System.Net.Http.Functional.Tests;
using System.Diagnostics;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write test that checks that a session will not timeout because of QUIC limit and that the session has a keepalive mechanism - use KeepAlivePingInterval and KeepAlivePingDelay on SocketsHttpHandler
// TODO: write a test that uses a proxy

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionTests : WebTransportTestBase, IAsyncDisposable
{

    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = [VariableLengthIntegerHelper.MinValue - 1, VariableLengthIntegerHelper.MaxValue + 1];

    public static readonly TheoryData<Func<WebTransportSession, CancellationToken, Task>> s_operationsAsParameters = [
        (session, cancellationToken) => session.SetUnidirectionalStreamCountLimitForPeerAsync(1, cancellationToken).AsTask(),
        (session, cancellationToken) => session.SetBidirectionalStreamCountLimitForPeerAsync(1, cancellationToken).AsTask(),
        (session, cancellationToken) => session.SetDataSentLimitForPeerAsync(1, cancellationToken).AsTask(),
        (session, cancellationToken) => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional, cancellationToken).AsTask(),
        (session, cancellationToken) => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional, cancellationToken).AsTask(),
        (session, cancellationToken) => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional, cancellationToken).AsTask(),
        (session, cancellationToken) => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional, cancellationToken).AsTask(),
        (session, cancellationToken) => session.RequestCloseAsync(cancellationToken).AsTask(),
        (session, cancellationToken) => session.CloseAsync(0, "", cancellationToken).AsTask(),
        ];

    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public async Task InvalidVariableLengthIntegerPassedToSessionConfigurationPropertiesThrows(long invalidVarInt)
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
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetUnidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetBidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetDataSentLimitForPeerAsync(invalidVarInt));

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
    [MemberData(nameof(s_operationsAsParameters))]
    public async Task OperationCanceledExceptionIsThrownWhenCancellationIsRequested(Func<WebTransportSession, CancellationToken, Task> operation)
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

            CancellationTokenSource cts = new();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(session, cts.Token));

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
    public async Task OpeningStreamBeforeSessionExistsWorks(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);
        ReadOnlyMemory<byte> dataToSend = new byte[] { 1, 2, 3, 4, 5 };

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession backgroundSession = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            await using WebTransportStream stream = await session.AcceptInboundStreamAsync(streamType);

            byte[] receivedData = new byte[dataToSend.Length];

            await stream.ReadExactlyAsync(receivedData);

            Assert.Equal(dataToSend, receivedData);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession backgroundSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            QuicStreamType quicStreamType = streamType switch
            {
                WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
                WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
                _ => throw new ArgumentOutOfRangeException(nameof(streamType), "Invalid stream type")
            };

            long nextSessionId = (int)backgroundSession.SessionId + 4;
            await using QuicStream stream = await backgroundSession.Connection.OpenQuicStreamAsync(quicStreamType);

            long streamTypeOrSignalValue = WebTransportStreamTypeHelper.GetStreamTypeOrSignalValue(streamType);

            VariableLengthIntegerStreamHelper.Write(stream, streamTypeOrSignalValue);
            VariableLengthIntegerStreamHelper.Write(stream, nextSessionId);
            await stream.WriteAsync(dataToSend);

            await using WebTransportServerSession session = await _webTransportServer.AcceptWebTransportServerSessionAsync(backgroundSession.Connection);

            Debug.Assert(session.SessionId == nextSessionId, $"Session ID prediction failed (expected: {nextSessionId}, received: {session.SessionId}).");

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(WebTransportStreamType.Unidirectional)]
    [InlineData(WebTransportStreamType.Bidirectional)]
    public async Task OpeningTooManyStreamsFromClientResultsInSuspension(WebTransportStreamType streamType)
    {
        using Barrier barrier = new(2);

        int maximumNumberOfQuicStreamsPerConnection = streamType switch
        {
            WebTransportStreamType.Unidirectional => _http3Options.MaxInboundUnidirectionalStreams,
            WebTransportStreamType.Bidirectional => _http3Options.MaxInboundBidirectionalStreams,
            _ => throw new ArgumentException(nameof(streamType)),
        };
        int numberOfStreamsUsedForConnectionAndSessionSetup = streamType switch
        {
            WebTransportStreamType.Unidirectional => 1, // HTTP connection control stream
            WebTransportStreamType.Bidirectional => 1, // WebTransport session CONNECT stream
            _ => throw new ArgumentException(nameof(streamType)),
        };

        int maxNumberOfWebTransportStreamsThatCanBeOpen = maximumNumberOfQuicStreamsPerConnection
            - numberOfStreamsUsedForConnectionAndSessionSetup;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            List<WebTransportStream> streams = new();

            for (int i = 0; i < maxNumberOfWebTransportStreamsThatCanBeOpen; i++)
            {
                streams.Add(await session.OpenOutboundStreamAsync(streamType));
            }

            CancellationTokenSource cts = new(5000);

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.OpenOutboundStreamAsync(streamType, cts.Token));

            await Task.WhenAll(streams.Select(s => s.DisposeAsync().AsTask()));

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
    public async Task OpeningTooManyStreamsFromServerWithoutAcceptanceResultsInNewStreamsBeingRejected(WebTransportStreamType streamType)
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

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            (List<QuicStream> openStreams, QuicStream rejectedStream) = await WebTransportSessionTestHelper.OpenMorePendingStreamsThanAllowed(serverSession, streamType);

            QuicException writesClosedEx = await Assert.ThrowsAsync<QuicException>(() => rejectedStream.WritesClosed);
            Assert.Equal(QuicError.StreamAborted, writesClosedEx.QuicError);
            Assert.Equal((long)Http3ErrorCode.WebTransportBufferedStreamRejected, writesClosedEx.ApplicationErrorCode);

            if (streamType == WebTransportStreamType.Bidirectional)
            {
                QuicException readsClosedEx = await Assert.ThrowsAsync<QuicException>(() => rejectedStream.ReadsClosed);
                Assert.Equal(QuicError.StreamAborted, readsClosedEx.QuicError);
                Assert.Equal((long)Http3ErrorCode.WebTransportBufferedStreamRejected, readsClosedEx.ApplicationErrorCode);
            }

            await Task.WhenAll(openStreams.Select(s => s.DisposeAsync().AsTask()));

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task PooledConnectionLifetimeDoesNotCloseWebTransportSession()
    {
        using Barrier barrier = new(2);

        WebTransportStreamType streamType = WebTransportStreamType.Unidirectional;
        Task clientTask = Task.Run(async () =>
        {

            TimeSpan pooledConnectionLifetime = TimeSpan.FromSeconds(10);
            SocketsHttpHandler handler = TestHelper.CreateSocketsHttpHandler(allowAllCertificates: true);
            // The following handler configuration makes it so that the connection pool manager cleans up the connection pools every second
            handler.PooledConnectionLifetime = pooledConnectionLifetime;
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(4);
            HttpClient client = new(handler);

            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = client,
                DefaultStreamErrorCode = 0
            });

            WebTransportStream stream = await session.OpenOutboundStreamAsync(streamType);

            CancellationTokenSource cts = new();
            Task sendDataTask = Task.Run(async () =>
            {
                using (stream)
                {
                    while (true)
                    {
                        await stream.WriteAsync(new byte[] { 1, 2, 3 });
                        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                    }
                }
            }, cts.Token);

            await Task.Delay(TimeSpan.FromSeconds(3)); // After some time the connection used by the WT session should have been removed from the pool

            cts.Cancel();

            Assert.Equal(WebTransportSessionState.Open, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            await using QuicStream stream = await serverSession.AcceptStreamFromServerAsync(streamType);

            byte[] buffer = new byte[10];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer);
                if (bytesRead == 0)
                {
                    break;
                }
            }

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}
