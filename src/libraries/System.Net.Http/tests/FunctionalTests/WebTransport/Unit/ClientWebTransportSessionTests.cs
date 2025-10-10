// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Threading.Tasks;

namespace System.Net.WebTransport.Unit.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class ClientWebTransportSessionTests : WebTransportTestBase
{
    [Fact]
    public async Task ConnectAsyncThrowsWhenCalledWithNullOptions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("options", () => ClientWebTransportSession.ConnectAsync(null));
    }
}
