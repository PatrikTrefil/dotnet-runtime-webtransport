// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;

namespace System.Net.WebTransport.Unit.Tests;

// TODO: move to unit tests
public abstract class WebTransportTestBase
{
    public static bool IsWebTransportSupported => QuicConnection.IsSupported;
}
