// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Functional.Tests;
using System.Threading.Tasks;
using System.Net.Test.Common;
using System.Net.Http;
using System.Net.Quic;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: there are many synchronizations that will be redundant after we get RESET_STREAM_AT support - remove those once it is available

public abstract class WebTransportTestBase : IAsyncDisposable
{
    public const long s_maxOpenWebTransportStreamsPerType = 65534;

    protected static bool IsWebTransportSupported => QuicConnection.IsSupported;
    protected virtual int TestTimeoutInMilliseconds => 200_000;

    protected readonly Http3LoopbackServer _httpServer;
    internal readonly WebTransportLoopbackServer _webTransportServer;
    protected readonly HttpClient _client;
    protected readonly Http3Options _http3Options = new Http3Options
    {
        QuicConnectionIdleTimeout = TimeSpan.FromHours(1),
        MaxInboundUnidirectionalStreams = 150,
        MaxInboundBidirectionalStreams = 150,
    };
    protected WebTransportSessionCreationOptions _defaultWebTransportSessionCreationOptions;

    /// <remarks>
    /// Default implementation provided by <see cref="WebTransportTestBase"/> is no limits.
    /// </remarks>
    internal virtual WebTransportHttpConnectionCreationOptions DefaultWebTransportHttpConnectionCreationOptions => new WebTransportHttpConnectionCreationOptions
    {
        MaxSessionCount = VariableLengthIntegerHelper.MaxValue,
        InitialDataSentLimitForPeer = VariableLengthIntegerHelper.MaxValue,
        InitialBidirectionalStreamCountLimitForPeer = s_maxOpenWebTransportStreamsPerType,
        InitialUnidirectionalStreamCountLimitForPeer = s_maxOpenWebTransportStreamsPerType,
    };

    public WebTransportTestBase()
    {
        _httpServer = (Http3LoopbackServer)Http3LoopbackServerFactory.Singleton.CreateServer(_http3Options);
        _webTransportServer = new WebTransportLoopbackServer(
            _httpServer,
            DefaultWebTransportHttpConnectionCreationOptions
            );

        var handler = new VersionHttpClientHandler(HttpVersion.Version30)
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates
        };
        _client = new HttpClient(handler);

        _defaultWebTransportSessionCreationOptions = new WebTransportSessionCreationOptions
        {
            Uri = _webTransportServer.Address,
            HttpMessageInvoker = _client,
            DefaultStreamErrorCode = 0,
            HttpVersion = HttpVersion.Version30,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

    }

    public async ValueTask DisposeAsync()
    {
        await _webTransportServer.DisposeAsync();
        _httpServer.Dispose();
        _client.Dispose();
    }
}
