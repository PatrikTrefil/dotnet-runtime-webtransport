// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Net.WebTransport.Functional.Tests;

internal class WebTransportStreamTypeHelper
{
    public static long UnidirectionalStreamTypeValue => 0x54;
    public static long BidirectionalStreamSignalValue => 0x41;

    public static long GetStreamTypeOrSignalValue(WebTransportStreamType streamType, [CallerArgumentExpression(nameof(streamType))] string? paramName = null)
    {
        return streamType switch
        {
            WebTransportStreamType.Unidirectional => UnidirectionalStreamTypeValue,
            WebTransportStreamType.Bidirectional => BidirectionalStreamSignalValue,
            _ => throw new ArgumentException("Unknown stream type or signal value.", paramName)
        };
    }
}
