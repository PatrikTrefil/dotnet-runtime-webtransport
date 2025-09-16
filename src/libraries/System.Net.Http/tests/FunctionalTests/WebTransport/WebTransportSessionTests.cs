// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Test.Common;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(WebTransportTestBase.IsWebTransportSupported))]
public sealed class WebTransportSessionTests : WebTransportTestBase
{
    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;

    private static long s_maxValidVariableLengthIntegerValue = (long)BigInteger.Pow(2, 62) - 1;
    private static long s_minValidVariableLengthIntegerValue = 0;

    private static readonly long[] s_validVariableLengthIntegers = [s_minValidVariableLengthIntegerValue , s_maxValidVariableLengthIntegerValue];
    private static readonly long[] s_invalidVariableLengthIntegers = [s_minValidVariableLengthIntegerValue - 1 , s_maxValidVariableLengthIntegerValue + 1];

    public static readonly IEnumerable<object[]> s_validVariableLengthIntegersAsParameters = s_validVariableLengthIntegers.Select(i => new object[] { i });
    public static readonly IEnumerable<object[]> s_invalidVariableLengthIntegersAsParameters = s_invalidVariableLengthIntegers.Select(i => new object[] { i });

    private static readonly Func<WebTransportSession, CancellationToken, Task>[] s_operations = [
        (session, cancellationToken) => session.SetUnidirectionalStreamCountLimitForPeerAsync(1, cancellationToken),
        (session, cancellationToken) => session.SetBidirectionalStreamCountLimitForPeerAsync(1, cancellationToken),
        (session, cancellationToken) => session.SetDataSentLimitForPeerAsync(1, cancellationToken),
        (session, cancellationToken) => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional, cancellationToken),
        (session, cancellationToken) => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional, cancellationToken),
        (session, cancellationToken) => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional, cancellationToken),
        (session, cancellationToken) => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional, cancellationToken),
        (session, cancellationToken) => session.RequestCloseAsync(cancellationToken),
        (session, cancellationToken) => session.CloseAsync(0, "", cancellationToken),
        (session, cancellationToken) => session.CloseAsync(0, ""u8.ToArray(), cancellationToken)
        ];
    public readonly static IEnumerable<object[]> s_operationsAsParameters = s_operations.Select(op => new object[] { op });

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ConnectionEstablishmentWithValidHandshakeSucceeds()
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
    public async Task ObjectDisposedExceptionIsThrownWhenAccessingPropertiesOfDisposedSession()
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
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetDataSentLimitForPeerAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RequestCloseAsync());
            Assert.Throws<ObjectDisposedException>(() => session.Close());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""u8.ToArray()));
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegersAsParameters))]
    public async Task InvalidVariableLengthIntegerPassedToSessionConfigurationPropertiesThrows(long invalidVarInt)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(invalidVarInt));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetDataSentLimitForPeerAsync(invalidVarInt));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await clientTask; // prevent server session from closing before client task runs
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [Theory]
    [MemberData(nameof(s_operationsAsParameters))]
    public async Task OperationCanceledExceptionIsThrownWhenCancellationIsRequested(Func<WebTransportSession, CancellationToken, Task> operation)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            CancellationTokenSource cts = new();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(session, cts.Token));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
    // TODO: add tests with multiple WT sessions
    // TODO: test that redirects don't connect
    // TODO: add test for connecting to a host that doesn't support WT
    // TODO: add test for connection to a non-existent host
    // TODO: add test for connection to a host that doesn't support HTTP/3
    // TODO: add test for connection to a host that doesn't support WT over HTTP/3
    // TODO: add test for connection to a host that performs invalid WT handshake
    // TODO: add test that makes two extended CONNECT requests and they should both return the exact same exception object

    // TODO: move these to unit tests
    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegersAsParameters))]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialUnidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialBidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialDataSentLimitForPeer = invalidVarInt });
    }

    [Theory]
    [MemberData(nameof(s_validVariableLengthIntegersAsParameters))]
    public void ValidVariableLengthIntegerUsedToCreateInitialSessionConfigurationDoesNotThrow(long validVarInt)
    {
        new WebTransportSessionCreationOptions() { InitialUnidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { InitialBidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { InitialDataSentLimitForPeer = validVarInt };
    }
}
