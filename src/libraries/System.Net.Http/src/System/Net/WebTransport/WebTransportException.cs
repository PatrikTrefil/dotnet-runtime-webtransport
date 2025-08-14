// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

public class WebTransportException : Exception
{
    public WebTransportException(string message) : base(message) { }
    public WebTransportException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class WebTransportControlStreamClosedException : Exception
{
    public WebTransportControlStreamClosedException() : base() { }
}

public sealed class WebTransportStreamClosedException : WebTransportException
{
    public int ApplicationErrorCode { get; }
    public WebTransportStreamClosedException(string message, int applicationErrorCode) : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
    }
    public WebTransportStreamClosedException(string message, int applicationErrorCode, Exception innerException) : base(message, innerException)
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
