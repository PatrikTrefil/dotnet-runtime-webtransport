// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

/// <summary>
/// Specifies the direction of the <see cref="WebTransportStream"/> which is to be aborted.
/// This enumeration supports a bitwise combination of its member values.
/// </summary>
/// <seealso cref="WebTransportStream.Abort(WebTransportAbortDirection, long)"/>
public enum WebTransportAbortDirection
{
    Read = 1,
    Write = 2,
    Both = 3
}
