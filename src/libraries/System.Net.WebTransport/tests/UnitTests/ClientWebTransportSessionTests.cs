// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Threading.Tasks;
using System.Net.Http;

namespace System.Net.WebTransport.Unit.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class ClientWebTransportSessionTests : WebTransportTestBase
{
    public static readonly TheoryData<Version, HttpVersionPolicy> s_invalidVersionRequirements = new TheoryData<Version, HttpVersionPolicy>() {
        { HttpVersion.Version10, HttpVersionPolicy.RequestVersionOrLower },
        { HttpVersion.Version10, HttpVersionPolicy.RequestVersionExact },
        { new Version(0, 0, 0), HttpVersionPolicy.RequestVersionExact }
    };

    [Theory]
    [MemberData(nameof(s_invalidVersionRequirements))]
    public async Task ConnectAsyncThrowsWhenRequiringDifferentHttpVersionThan30(Version version, HttpVersionPolicy policy)
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => ClientWebTransportSession.ConnectAsync(
            new WebTransportSessionCreationOptions
            {
                Uri = new Uri("https://example.com"),
                DefaultStreamErrorCode = 0,
                HttpMessageInvoker = new HttpClient(),
                HttpVersion = version,
                HttpVersionPolicy = policy
            }));
    }
}
