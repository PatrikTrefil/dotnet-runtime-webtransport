// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

public sealed class WebTransportException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="message">The message for the exception</param>
    public WebTransportException(WebTransportError error, string message) : this(error, null, null, message, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public WebTransportException(WebTransportError error, string message, Exception? innerException) : this(error, null, null, message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="applicationErrorCode">The application error code associated with the error.</param>
    /// <param name="applicationErrorMessage">The application error message associated with the error.</param>
    /// <param name="message">The message for the exception</param>

    public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message) : this(error, applicationErrorCode, applicationErrorMessage, message, null) { }
    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="applicationErrorCode">The application error code associated with the error.</param>
    /// <param name="applicationErrorMessage">The application error message associated with the error.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>

    public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message, Exception? innerException) : base(message, innerException)
    {
        WebTransportError = error;
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }

    /// <summary>
    /// Gets the error that's associated with this exception.
    /// </summary>
    public WebTransportError WebTransportError { get; }

    /// <summary>
    /// Error code provided by the peer when closing a stream/session.
    /// The value is in the range [0, 2^32).
    /// </summary>
    public long? ApplicationErrorCode { get; }

    /// <summary>
    /// Application error message provided by the peer when closing a session.
    /// When the session has been closed by peer using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session has been closed cleanly by peer using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public string? ApplicationErrorMessage { get; }
}
