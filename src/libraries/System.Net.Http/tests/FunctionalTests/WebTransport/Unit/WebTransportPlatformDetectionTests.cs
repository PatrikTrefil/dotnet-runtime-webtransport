// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using System.Net.Quic;

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
        PlatformNotSupportedException listenerEx = await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await ClientWebTransportSession.ConnectAsync(null, null, null));
    }

    [ConditionalFact(nameof(IsQuicSupported))]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void SupportedWindowsPlatforms_IsSupportedIsTrue()
    {
        Assert.True(ClientWebTransportSession.IsSupported);
    }


    [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsInHelix))]
    [PlatformSpecific(TestPlatforms.Linux)]
    public void SupportedLinuxPlatforms_IsSupportedIsTrue()
    {
        _output.WriteLine($"Running on {PlatformDetection.GetDistroVersionString()}");
        Assert.True(ClientWebTransportSession.IsSupported);
    }
}
