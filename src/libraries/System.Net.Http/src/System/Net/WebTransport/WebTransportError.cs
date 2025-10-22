// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

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
    /// The connection was aborted by the peer. This error is associated with a close status code and a close status description.
    /// If the close status code and close status descrption were provided by the peer, they are stored in <see cref="WebTransportException.CloseStatusDescription"/> and <see cref="WebTransportException.CloseStatusCode"/>.
    /// </summary>
    SessionClosedByPeer = 2,
    /// <summary>
    /// The read or write direction of the stream was aborted by the peer. This error is associated with a close status code.
    /// If a valid status code was provided, it is stored in <see cref="WebTransportException.CloseStatusCode"/>.
    /// </summary>
    StreamAborted = 3,
    /// <summary>
    /// An error on the transport layer occured.
    /// </summary>
    TransportLayerError = 4,
    /// <summary>
    /// The server refused the session.
    /// </summary>
    SessionConnectFailure = 5,
    /// <summary>
    /// The operation has been aborted.
    /// </summary>
    OperationAborted = 6,
    /// <summary>
    /// An error occurred in the user provided callback.
    /// </summary>
    CallbackError = 7,
    /// <summary>
    /// Indicates that the client requested an unsupported WebTransport subprotocol.
    /// </summary>
    UnsupportedProtocol = 8,
    /// <summary>
    /// Indicates an error occurred when parsing the HTTP headers during the opening handshake.
    /// </summary>
    HeaderError = 9,
    /// <summary>
    /// Indicates that the server responded with a redirect and the client must follow it to establish the session.
    /// </summary>
    /// <remarks>
    /// When a <see cref="WebTransportException"/> with <see cref="RedirectRequired"/> is thrown, the property <see cref="WebTransportException.RedirectLocation"/> contains the URI to redirect to.
    /// </remarks>
    RedirectRequired = 10
}
