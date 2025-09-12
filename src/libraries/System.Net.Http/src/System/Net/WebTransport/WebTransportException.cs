// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

// TODO: redo exception hierarchy similar to QUIC
public class WebTransportException : Exception
{
    public WebTransportException(string message) : base(message) { }

    public WebTransportException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class WebTransportStreamClosedException : WebTransportException
{
    /// <summary>
    /// Error code provided by the peer when closing the stream.
    /// The value is in the range [0, 2^32).
    /// </summary>
    public long ApplicationErrorCode { get; }

    public WebTransportStreamClosedException(string message, long applicationErrorCode) : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
    }

    public WebTransportStreamClosedException(string message, long applicationErrorCode, Exception innerException) : base(message, innerException)
    {
        ApplicationErrorCode = applicationErrorCode;
    }
}

public sealed class WebTransportSessionClosedException : WebTransportException
{
    public int ApplicationErrorCode { get; }
    public string ApplicationErrorMessage { get; }

    public WebTransportSessionClosedException(string message, int applicationErrorCode, string applicationErrorMessage) : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }

    public WebTransportSessionClosedException(string message, int applicationErrorCode, string applicationErrorMessage, Exception innerException) : base(message, innerException)
    {
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }
}
