// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Http.Functional.Tests;

namespace System.Net.WebTransport.Functional.Tests;

public class WebTransportTestBase : HttpClientHandlerTestBase
{
    public WebTransportTestBase(ITestOutputHelper output) : base(output) { }
    protected override Version UseVersion => HttpVersion.Version30;
    public static bool IsWebTransportSupported => WebTransportSession.IsSupported;
}
