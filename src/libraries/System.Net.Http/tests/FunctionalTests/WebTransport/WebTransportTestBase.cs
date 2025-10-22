// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Http.Functional.Tests;
using System.Threading.Tasks;
using System.Net.Test.Common;
using System.Net.Http;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: there are many synchronizations that will be redundant after we get RESET_STREAM_AT support - remove those once it is available

public abstract class WebTransportTestBase: IAsyncDisposable
{
    public static bool IsWebTransportSupported => ClientWebTransportSession.IsSupported;
    public virtual int TestTimeoutInMilliseconds => 200_000;

    internal readonly Http3LoopbackServer _httpServer;
    internal readonly WebTransportLoopbackServer _webTransportServer;
    internal readonly HttpClient _client;
    internal readonly Http3Options _http3Options = new Http3Options
    {
        QuicConnectionIdleTimeout = TimeSpan.FromHours(1),
        MaxInboundUnidirectionalStreams = 150,
        MaxInboundBidirectionalStreams = 150,
    };

    public WebTransportTestBase()
    {
        _httpServer = (Http3LoopbackServer)Http3LoopbackServerFactory.Singleton.CreateServer(_http3Options);
        _webTransportServer = new WebTransportLoopbackServer(_httpServer);

        var handler = new VersionHttpClientHandler(HttpVersion.Version30);
        handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
        _client = new HttpClient(handler);
    }

    public async ValueTask DisposeAsync()
    {
        await _webTransportServer.DisposeAsync();
        _httpServer.Dispose();
        _client.Dispose();
    }
}
