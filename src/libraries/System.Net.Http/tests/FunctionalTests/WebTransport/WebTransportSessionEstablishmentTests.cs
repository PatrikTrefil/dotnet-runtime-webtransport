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
public sealed class WebTransportSessionEstablishmentTests : WebTransportTestBase
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
            Assert.Equal(WebTransportError.SessionConnectFailure, ex.WebTransportError);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    /// <summary>
    /// The point of this test is to verify that the session reserved by a failed attempt is released properly.
    /// If it was not, the second attempt would fail due to max sessions limit being reached.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SessionEstablishmentSucceedsAfterFailedAttempt(HttpStatusCode statusCode)
    {
        using Barrier barrier = new(2);

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );
            HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);

            await connection.SendResponseAsync(statusCode: statusCode, content: null, isFinal: false);

            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync(connection);

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
            Assert.Equal(WebTransportError.SessionConnectFailure, ex.WebTransportError);

            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task SessionEstablishmentThrowsWhenTheServerResponsedWithRedirectAndTheHttpMessageInvokerDoesNotAutoRedirect(HttpStatusCode statusCode)
    {
        using Barrier barrier = new(2);
        string expectedLocation = "https://host/redirected";

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );
            HttpRequestData httpRequestData = await connection.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);

            List<HttpHeaderData> headers = [
                new HttpHeaderData("Location", expectedLocation)
            ];
            await connection.SendResponseAsync(statusCode: statusCode, headers, content: null, isFinal: false);

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
            Assert.Equal(WebTransportError.RedirectRequired, ex.WebTransportError);
            Assert.Equal(expectedLocation, ex.RedirectLocation.ToString());

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task SessionEstablishmentSucceedsWhenTheServerResponsedWithRedirectAndTheHttpMessageInvokerDoesAutoRedirect(HttpStatusCode statusCode)
    {
        using Barrier barrier = new(2);
        Http3LoopbackServer httpServerWithRedirect = new();

        Task serverTask = Task.Run(async () =>
        {
            await using Http3LoopbackConnection connectionWithRedirect = await httpServerWithRedirect.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );

            HttpRequestData httpRequestData = await connectionWithRedirect.ReadRequestDataAsync(readBody: false).ConfigureAwait(false);

            List<HttpHeaderData> headers = [
                new HttpHeaderData("Location", _httpServer.Address.ToString())
            ];

            await connectionWithRedirect.SendResponseAsync(statusCode: statusCode, headers, content: null, isFinal: false);

            await using Http3LoopbackConnection connection = await _httpServer.EstablishConnectionAsync(
                new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 }
            );

            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptWebTransportServerSessionAsync(connection);

            barrier.SignalAndWait(TestTimeoutInMilliseconds);
        });

        Task clientTask = Task.Run(async () =>
        {
            VersionHttpClientHandler handler = new(HttpVersion.Version30) { AllowAutoRedirect = true };
            handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
            HttpClient client = new(handler);

            await using WebTransportSession sesison = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = httpServerWithRedirect.Address,
                HttpMessageInvoker = client,
                DefaultStreamErrorCode = 0
            });

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
