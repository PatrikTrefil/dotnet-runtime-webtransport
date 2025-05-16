// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

public class WebTransportException : Exception
{
    public WebTransportException(string message) : base(message) { }
    public WebTransportException(string message, Exception innerException) : base(message, innerException) { }
}
