// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class WebTransportSessionConfigurationSubprotocolTests : WebTransportTestBase
{
    public static readonly TheoryData<string[]> s_offeredSubprotocols = [
        [],
        [""],
        ["abc", "bcd"]
        ];
    private static readonly string s_notOfferedSubprotocol = "notOffered";

    public WebTransportSessionConfigurationSubprotocolTests(ITestOutputHelper output) : base(output) { }


    [Theory]
    [InlineData("abc")]
    [InlineData("*bcd")]
    [InlineData("")]
    public async Task SubprotocolSuccessfulNegotiationTest(string expectedSubprotocol)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                AvailableSubProtocols = [expectedSubprotocol]
            };
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client, options);

            Assert.Equal(expectedSubprotocol, session.SubProtocol);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync(expectedSubprotocol);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_offeredSubprotocols))]
    public async Task SubprotocolUnsuccessfulNegotiationTest(string[] offeredSubprotocols)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                AvailableSubProtocols = offeredSubprotocols
            };
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client, options);

            Assert.Null(session.SubProtocol);

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
    [MemberData(nameof(s_offeredSubprotocols))]
    public async Task ServerChoosesSubprotocolThatWasNotOfferedThrowsOnClient(string[] offeredSubprotocols)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                AvailableSubProtocols = offeredSubprotocols
            };

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client, options));
            Assert.Equal(WebTransportError.HeaderError, ex.WebTransportError);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync(s_notOfferedSubprotocol);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [MemberData(nameof(s_offeredSubprotocols))]
    public async Task ServerRespondsWithInvalidToken(string[] offeredSubprotocols)
    {
        const string s_invalidSubprotocol = "1bc";
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                AvailableSubProtocols = offeredSubprotocols
            };

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => ClientWebTransportSession.ConnectAsync(_webTransportServer.Address, _client, options));
            Assert.Equal(WebTransportError.HeaderError, ex.WebTransportError);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.CreateWebTransportServerSessionAsync(s_invalidSubprotocol);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}
