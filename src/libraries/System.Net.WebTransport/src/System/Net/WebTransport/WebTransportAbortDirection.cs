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
    /// <summary>
    /// Abort the read side of the stream.
    /// </summary>
    Read = 1,

    /// <summary>
    /// Abort the write side of the stream.
    /// </summary>
    Write = 2,

    /// <summary>
    /// Abort both the read and write sides of the stream.
    /// </summary>
    Both = 3
}
