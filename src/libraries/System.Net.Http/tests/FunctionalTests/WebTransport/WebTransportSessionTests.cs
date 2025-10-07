// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Test.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write test when server opens a stream for a non-existing session and then client opens a session with that id (implementation easy if we can predict the session id, otherwise we have to do manual session establishment)
// TODO: write test when a CONNECT request fails (e.g. timeout) and then check if the connection is closed by client (it should because it is not used)
// TODO: write test where client tries to open a WebTransportSession to a server that doesn't support WT over HTTP/3

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
        (session, cancellationToken) => session.RequestCloseAsync(cancellationToken),
        (session, cancellationToken) => session.CloseAsync(0, "", cancellationToken),
        ];

    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }


    [Fact]
    public async Task ConnectionEstablishmentWithValidHandshakeSucceeds()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Fact]
    public async Task ObjectDisposedExceptionIsThrownWhenAccessingPropertiesOfDisposedSession()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSession session;
            await using (session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client)) { }
            await WebTransportSessionTestHelper.AssertAllOperationsOnSessionThrowAsync<ObjectDisposedException>(session, null);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public async Task InvalidVariableLengthIntegerPassedToSessionConfigurationPropertiesThrows(long invalidVarInt)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetUnidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetBidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("limit", async () => await session.SetDataSentLimitForPeerAsync(invalidVarInt));

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
    [MemberData(nameof(s_operationsAsParameters))]
    public async Task OperationCanceledExceptionIsThrownWhenCancellationIsRequested(Func<WebTransportSession, CancellationToken, Task> operation)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);

            CancellationTokenSource cts = new();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(session, cts.Token));

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
    // TODO: add tests with multiple WT sessions
    // TODO: test that redirects don't connect
    // TODO: add test for connecting to a host that doesn't support WT
    // TODO: add test for connection to a non-existent host
    // TODO: add test for connection to a host that doesn't support HTTP/3
    // TODO: add test for connection to a host that doesn't support WT over HTTP/3
    // TODO: add test for connection to a host that performs invalid WT handshake
    // TODO: add test that makes two extended CONNECT requests and they should both return the exact same exception object
}
