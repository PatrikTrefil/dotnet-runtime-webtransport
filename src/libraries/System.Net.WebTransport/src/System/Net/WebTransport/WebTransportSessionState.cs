// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

/// <summary>
/// Represents the current lifecycle state of a <see cref="WebTransportSession"/>.
/// </summary>
public enum WebTransportSessionState
{
    /// <summary>
    /// The session has not been established yet.
    /// </summary>
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
    /// The session was aborted by the local endpoint.
    /// </summary>
    /// <remarks>
    /// This state is used when the local implementation aborts the session, for example:
    /// <list type="bullet">
    /// <item><description>An error occurs while processing incoming capsules, including invalid or unsupported configuration received from the peer.</description></item>
    /// <item><description><see cref="WebTransportSessionCreationOptions.GracefulShutdownHandler"/> throws while handling a peer-initiated graceful shutdown.</description></item>
    /// </list>
    /// </remarks>
    AbortedLocally,
    /// <summary>
    /// The session was closed abortively by peer.
    /// </summary>
    AbortedRemotely
}
