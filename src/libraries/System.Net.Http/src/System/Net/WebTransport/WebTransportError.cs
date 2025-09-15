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
    /// The connection was aborted by the peer. This error is associated with an application error code and an application error message.
    /// </summary>
    SessionClosed = 2,
    /// <summary>
    /// The read or write direction of the stream was aborted by the peer. This error is associated with an application error code.
    /// </summary>
    StreamAborted = 3,
    /// <summary>
    /// An error on the transport layer occured.
    /// </summary>
    TransportLayerError = 4,
    /// <summary>
    /// The server refused the session.
    /// </summary>
    SessionRefused = 5,
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
    HeaderError = 9
}
