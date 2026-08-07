// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

/// <summary>
/// Specifies WebTransport error categories returned by <see cref="WebTransportException"/>.
/// </summary>
public enum WebTransportError
{
    /// <summary>
    /// No error.
    /// </summary>
    Success = 0,
    /// <summary>
    /// An internal implementation error occurred.
    /// </summary>
    InternalError = 1,
    /// <summary>
    /// The session was closed gracefully by the peer. This error is associated with a close status code and a close status description.
    /// The close status code and the close status description provided by the peer are stored in <see cref="WebTransportException.CloseStatusCode"/> and <see cref="WebTransportException.CloseStatusDescription"/>.
    /// </summary>
    SessionClosedByPeer = 2,
    /// <summary>
    /// The session was aborted by the peer.
    /// </summary>
    /// <remarks>
    /// An abortive termination carries no close status information, so <see cref="WebTransportException.CloseStatusCode"/> and <see cref="WebTransportException.CloseStatusDescription"/> are <c>null</c>.
    /// </remarks>
    SessionAbortedByPeer = 3,
    /// <summary>
    /// The read or write direction of the stream was aborted by the peer. This error is associated with a close status code.
    /// If a valid status code was provided, it is stored in <see cref="WebTransportException.CloseStatusCode"/>.
    /// </summary>
    StreamAborted = 4,
    /// <summary>
    /// An error on the transport layer occurred.
    /// </summary>
    TransportLayerError = 5,
    /// <summary>
    /// The server refused the session.
    /// </summary>
    SessionConnectFailure = 6,
    /// <summary>
    /// The operation has been aborted.
    /// </summary>
    OperationAborted = 7,
    /// <summary>
    /// An error occurred in the user provided callback.
    /// </summary>
    CallbackError = 8,
    /// <summary>
    /// Indicates that the client requested an unsupported WebTransport subprotocol.
    /// </summary>
    UnsupportedProtocol = 9,
    /// <summary>
    /// Indicates an error occurred when parsing the HTTP headers during the opening handshake.
    /// </summary>
    HeaderError = 10,
    /// <summary>
    /// Indicates that the server responded with a redirect and the client must follow it to establish the session.
    /// </summary>
    /// <remarks>
    /// When a <see cref="WebTransportException"/> with <see cref="RedirectRequired"/> is thrown, the property <see cref="WebTransportException.RedirectLocation"/> contains the URI to redirect to.
    /// </remarks>
    RedirectRequired = 11,
    /// <summary>
    /// Indicates that some limit has been exceeded.
    /// </summary>
    LimitExceeded = 12
}
