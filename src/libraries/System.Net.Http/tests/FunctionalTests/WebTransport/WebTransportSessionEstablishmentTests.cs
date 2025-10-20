// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;
using System.Net.Test.Common;
using System.Net.Quic;
using System.Threading;
using System.Net.Http;

namespace System.Net.WebTransport.Functional.Tests;


[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionEstablishmentTests : WebTransportTestBase, IAsyncDisposable
{
    [Fact]
    public async Task SessionEstablishmentWithValidHandshakeSucceeds()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task EstablishmentOfMultipleSessionsOverASingleConnectionSucceds()
    {
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession1 = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();
            await using WebTransportServerSession serverSession2 = await _webTransportServer.AcceptWebTransportServerSessionAsync(serverSession1.Connection);
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session1 = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            await using WebTransportSession session2 = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task EstablishmentOfMoreSesssionsThanAllowedThrows()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession1 = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync(maxSessionCount: 1);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session1 = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(() => ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Fact]
    public async Task SessionEstablishmentWithNonExistingEndpointThrows()
    {

        WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
        {
            Uri = new Uri("https://localhost/doesnotexit"),
            HttpMessageInvoker = _client,
            DefaultStreamErrorCode = 0
        }));
        Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SessionEstablishmentFailsWhenTheServerResponsedWithNonSuccessStatusCode(HttpStatusCode statusCode)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );
            HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);
            QuicStream controlStream = connection.CurrentStream.Stream;

            await connection.SendResponseAsync(statusCode: statusCode, content: null, isFinal: false);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionEstablishmentFailsWhenTheExtendedConnectRequestReachesTimeout()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            _client.Timeout = TimeSpan.FromMilliseconds(100);
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Fact]
    public async Task SessionEstablishmentFailsWhenTheServerDoesNotIndicateSupportForExtendedConnect()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionEstablishmentFailsWhenTheServerDoesNotHaveWebTransportMaxSessionsSetting()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 }
            );

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionEstablishmentFailsWhenTheServerHasWebTransportMaxSessionsSettingSetToZero()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 0 }
            );

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportException ex = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            }));
            Assert.Equal(WebTransportError.SessionRefused, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SessionEstablishmentFailsWhenTheServerDoesNotIndicateSupportForWebTransportEvenWhenAttemptedMultipleTimes()
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 }
            );

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            };

            WebTransportException ex1 = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(options));
            WebTransportException ex2 = await Assert.ThrowsAsync<WebTransportException>(async () => await ClientWebTransportSession.ConnectAsync(options));

            Assert.Equal(WebTransportError.SessionRefused, ex1.WebTransportError);
            Assert.Equal(WebTransportError.SessionRefused, ex2.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}
