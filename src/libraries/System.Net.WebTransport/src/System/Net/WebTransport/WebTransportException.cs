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
    public WebTransportException(WebTransportError error, string message) : this(error, null, null, null, message, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="redirectUri">Value of <see cref="Http.Headers.HttpResponseHeaders.Location"/> of the extended CONNECT request.</param>
    public WebTransportException(WebTransportError error, string message, Uri? redirectUri) : this(error, null, null, redirectUri, message, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public WebTransportException(WebTransportError error, string message, Exception? innerException) : this(error, null, null, null, message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="applicationErrorCode">The application error code associated with the error.</param>
    /// <param name="applicationErrorMessage">The application error message associated with the error.</param>
    /// <param name="message">The message for the exception</param>

    public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message) : this(error, applicationErrorCode, applicationErrorMessage, null, message, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="applicationErrorCode">The application error code associated with the error.</param>
    /// <param name="applicationErrorMessage">The application error message associated with the error.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message, Exception? innerException) : this(error, applicationErrorCode, applicationErrorMessage, null, message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebTransportException"/> class.
    /// </summary>
    /// <param name="error">The error associated with the exception.</param>
    /// <param name="applicationErrorCode">The application error code associated with the error.</param>
    /// <param name="applicationErrorMessage">The application error message associated with the error.</param>
    /// <param name="redirectUri">Redirect to this URI is required to create a session.</param>
    /// <param name="message">The message for the exception</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>

    public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, Uri? redirectUri, string message, Exception? innerException) : base(message, innerException)
    {
        RedirectLocation = redirectUri;
        WebTransportError = error;
        CloseStatusCode = applicationErrorCode;
        CloseStatusDescription = applicationErrorMessage;
    }

    /// <summary>
    /// Gets the error that's associated with this exception.
    /// </summary>
    public WebTransportError WebTransportError { get; }

    /// <summary>
    /// Error code provided by the peer when closing a stream/session.
    /// </summary>
    /// <value>
    /// The value is in the range [0, 2^32) or <c>null</c>.
    /// The value is not <c>null</c> if the <see cref="WebTransportError"/> is <see cref="WebTransportError.SessionClosedByPeer"/> and the peer provided a description when closing the session
    /// or the <see cref="WebTransportError"/> is <see cref="WebTransportError.StreamAborted"/>.
    /// </value>
    public long? CloseStatusCode { get; }

    /// <summary>
    /// Application error message provided by the peer when closing a session.
    /// </summary>
    /// <value>
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// The value is not <c>null</c>, if the <see cref="WebTransportError"/> is <see cref="WebTransportError.SessionClosedByPeer"/> and the peer provided a description when closing the session.
    /// </value>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public string? CloseStatusDescription { get; }

    /// <summary>
    /// Value of <see cref="Http.Headers.HttpResponseHeaders.Location"/> of the extended CONNECT request.
    /// </summary>
    /// <value>
    /// Value is not <c>null</c> if the <see cref="WebTransportError"/> is <see cref="WebTransportError.RedirectRequired"/>.
    /// </value>
    public Uri? RedirectLocation { get; }
}
