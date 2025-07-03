// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

/// <summary>
/// Represents the type of a stream.
/// </summary>
public enum WebTransportStreamType
{
    /// <summary>
    /// Write-only for the peer that opened the stream. Read-only for the peer that accepted the stream.
    /// </summary>
    Unidirectional,
    /// <summary>
    /// Both peers are read and write capable.
    /// </summary>
    Bidirectional
}
