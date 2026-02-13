// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

public enum WebTransportSessionState
{
    None = 0,
    /// <summary>
    /// The initial handshake has been completed and the session is open.
    /// </summary>
    Open,
    /// <summary>
    /// The session was closed by the remote peer.
    /// </summary>
    ClosedRemotely,
    /// <summary>
    /// The session was closed locally by the user.
    /// </summary>
    ClosedLocally,
    /// <summary>
    /// The session was closed due to a protocol violation by the remote peer.
    /// </summary>
    AbortedLocally,
    /// <summary>
    /// The session was closed abortively by peer.
    /// </summary>
    AbortedRemotely
}
