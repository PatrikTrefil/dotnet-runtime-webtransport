// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using System.Net.Quic;
using System.Net.Http;

namespace System.Net.WebTransport.Unit.Tests;

public sealed class WebTransportPlatformDetectionTests : WebTransportTestBase
{
    public static bool IsWebTransportUnsupported => !IsWebTransportSupported;
    public static bool IsQuicSupported => QuicConnection.IsSupported;

    private readonly ITestOutputHelper _output;
    public WebTransportPlatformDetectionTests(ITestOutputHelper output) : base()
    {
        _output = output;
    }

    [ConditionalFact(nameof(IsWebTransportUnsupported))]
    public async Task UnsupportedPlatforms_ThrowsPlatformNotSupportedException()
    {
        PlatformNotSupportedException listenerEx = await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
        {
            HttpMessageInvoker = new HttpClient(),
            Uri = new Uri("https://example.com"),
            DefaultStreamErrorCode = 0,
            HttpVersion = HttpVersion.Version30,
        }));
    }
}
