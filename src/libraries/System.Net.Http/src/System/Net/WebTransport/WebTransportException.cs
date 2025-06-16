// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

public class WebTransportException : Exception
{
    internal WebTransportException(string message) : base(message) { }
    internal WebTransportException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class WebTransportStreamClosedException : WebTransportException
{
    public int ApplicationErrorCode { get; }
    internal WebTransportStreamClosedException(string message, int applicationErrorCode) : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
    }
    internal WebTransportStreamClosedException(string message, int applicationErrorCode, Exception innerException) : base(message, innerException)
    {
        ApplicationErrorCode = applicationErrorCode;
    }
}

public sealed class WebTransportSessionClosedException : WebTransportException
{
    public int ApplicationErrorCode { get; }
    public string ApplicationErrorMessage { get; }
    internal WebTransportSessionClosedException(string message, int applicationErrorCode, string applicationErrorMessage) : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }
    internal WebTransportSessionClosedException(string message, int applicationErrorCode, string applicationErrorMessage, Exception innerException) : base(message, innerException)
    {
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }
}
