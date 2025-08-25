// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Test.Common;
using System.Numerics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionTests : WebTransportTestBase
{
    public WebTransportSessionTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;

    private static long s_maxValidVariableLengthIntegerValue = (long)BigInteger.Pow(2, 62) - 1;
    private static long s_minValidVariableLengthIntegerValue = 0;

    private static long[] s_validVariableLengthIntegers = [s_minValidVariableLengthIntegerValue , s_maxValidVariableLengthIntegerValue];
    private static long[] s_invalidVariableLengthIntegers = [s_minValidVariableLengthIntegerValue - 1 , s_maxValidVariableLengthIntegerValue + 1];

    public static readonly IEnumerable<object[]> s_validVariableLengthIntegersAsParameters = s_validVariableLengthIntegers.Select(i => new object[] { i });
    public static readonly IEnumerable<object[]> s_invalidVariableLengthIntegersAsParameters = s_invalidVariableLengthIntegers.Select(i => new object[] { i });

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
            Assert.Throws<ObjectDisposedException>(() => session.Close());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CloseAsync(0, ""u8.ToArray()));
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
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
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetMaxDataSentLimitForPeerAsync(invalidVarInt));
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await clientTask; // prevent server session from closing before client task runs
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    // TODO: move these to unit tests
    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(s_invalidVariableLengthIntegersAsParameters))]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxUnidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxBidirectionalStreamCount = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialMaxData = invalidVarInt });
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(s_validVariableLengthIntegersAsParameters))]
    public void ValidVariableLengthIntegerUsedToCreateInitialSessionConfigurationDoesNotThrow(long validVarInt)
    {
        new WebTransportSessionCreationOptions() { InitialMaxUnidirectionalStreamCount = validVarInt };
        new WebTransportSessionCreationOptions() { InitialMaxBidirectionalStreamCount = validVarInt };
        new WebTransportSessionCreationOptions() { InitialMaxData = validVarInt };
    }
}
