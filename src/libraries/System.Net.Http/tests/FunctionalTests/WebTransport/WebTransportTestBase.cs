// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Http.Functional.Tests;
using System.Threading.Tasks;
using System.Net.Test.Common;
using System.Net.Http;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

public abstract class WebTransportTestBase : HttpClientHandlerTestBase
{
    protected override Version UseVersion => HttpVersion.Version30;
    public static bool IsWebTransportSupported => WebTransportSession.IsSupported;
    public virtual int TestTimeout => 20_000;

    internal readonly Http3LoopbackServer _httpServer;
    internal readonly WebTransportLoopbackServer _webTransportServer;
    internal readonly HttpClient _client;

    public WebTransportTestBase(ITestOutputHelper output) : base(output) {
        _httpServer = CreateHttp3LoopbackServer();
        _webTransportServer = new WebTransportLoopbackServer(_httpServer);
        _client = CreateHttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _webTransportServer.DisposeAsync();
        _httpServer.Dispose();
        _client.Dispose();
    }
}
