// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write test when a CONNECT request fails (e.g. timeout) and then check if the connection is closed by client (it should because it is not used)
// TODO: write test where client tries to open a WebTransportSession to a server that doesn't support WT over HTTP/3
// TODO: write test where client tries to open a WebTransportSession to a server that doesn't support HTTP/3
// TODO: write test where client tries to open a WebTransportSession to a server that doesn't support extended connect
// TODO: write test where the client tries to open more session that allowed
// TODO: test that redirects don't connect
// TODO: add test that makes two extended CONNECT requests that both fail during server settings validation and they should both return the exact same exception object - validate caching of exceptions
// TODO: add test for connecting to a host that doesn't support WT
// TODO: add test for connection to a non-existent host
// TODO: add test for connection to a host that doesn't support HTTP/3
// TODO: add test for connection to a host that doesn't support WT over HTTP/3
// TODO: add test for connection to a host that performs invalid WT handshake
// TODO: add tests for establishing multiple connections over a simple HTTP connection
// TODO: add tests for connecting to a server that does not support RESET_STREAM_AT (write the test but disable it for now)

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionEstablishmentTests : WebTransportTestBase, IAsyncDisposable
{

    public WebTransportSessionEstablishmentTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task ConnectionEstablishmentWithValidHandshakeSucceeds()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

}
