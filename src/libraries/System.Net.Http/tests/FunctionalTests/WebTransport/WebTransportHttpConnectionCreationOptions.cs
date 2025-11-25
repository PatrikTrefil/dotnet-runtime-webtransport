// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.WebTransport.Functional.Tests;

public class WebTransportHttpConnectionCreationOptions
{
    public required long MaxSessionCount { get; init; }
    public long InitialUnidirectionalStreamCountLimitForPeer { get; init; } = 0;
    public long InitialBidirectionalStreamCountLimitForPeer { get; init; } = 0;
    public long InitialDataSentLimitForPeer { get; init; } = 0;
}
