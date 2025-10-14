// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Test.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write test when server opens a stream for a non-existing session and then client opens a session with that id (implementation easy if we can predict the session id, otherwise we have to do manual session establishment)
// TODO: add tests with multiple WT sessions and try opening streams and sending data
// TODO: add test for what happens if the QuicConnection is closed while a session is open
// TODO: write test that checks that a session will not timeout because of QUIC limit and that the session has a keepalive mechanism

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

    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();

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

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.OpenOutboundStreamAsync(streamType, cts.Token)); // TODO: document this behavior in conceptual docs

            await Task.WhenAll(streams.Select(s => s.DisposeAsync().AsTask()));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();

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
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();

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
}
