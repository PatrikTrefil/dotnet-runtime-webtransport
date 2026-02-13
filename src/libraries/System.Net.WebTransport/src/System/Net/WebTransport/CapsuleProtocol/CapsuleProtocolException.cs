// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport;

internal sealed class CapsuleProtocolException : Exception
{
    public CapsuleProtocolException(string message) : base(message) {}
    public CapsuleProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
