// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionTests : WebTransportTestBase
{
    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async void ConnectionEstablishmentWithValidHandshakeSucceeds()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }


    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async void ObjectDisposedExceptionIsThrownWhenAccessingPropertiesOfDisposedSession()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            WebTransportSession session;
            await using (session = await WebTransportSession.ConnectAsync(server.Address, client)) { }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetMaxDataSentLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RequestCloseAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""u8.ToArray()));
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task InvalidVariableLengthIntegerPassedToSessionConfigurationPropertiesThrows(long invalidVarInt)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetMaxDataSentLimitForPeerAsync(invalidVarInt));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await clientTask; // prevent server session from closing before client task runs
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    // TODO: write positive tests case for session config
    // TODO: move these to unit tests
    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxUnidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxBidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxData = invalidVarInt });
    }
}
