// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Numerics;
using Xunit;

namespace System.Net.WebTransport.Unit.Tests;

public class WebTransportSessionTests : WebTransportTestBase
{
    private static long s_maxValidVariableLengthIntegerValue = (long)BigInteger.Pow(2, 62) - 1;
    private const long s_minValidVariableLengthIntegerValue = 0;

    public static readonly TheoryData<long> s_validVariableLengthIntegers = [s_minValidVariableLengthIntegerValue, s_maxValidVariableLengthIntegerValue];
    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = [s_minValidVariableLengthIntegerValue - 1, s_maxValidVariableLengthIntegerValue + 1];

    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialUnidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialBidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { InitialDataSentLimitForPeer = invalidVarInt });
    }

    [Theory]
    [MemberData(nameof(s_validVariableLengthIntegers))]
    public void ValidVariableLengthIntegerUsedToCreateInitialSessionConfigurationDoesNotThrow(long validVarInt)
    {
        new WebTransportSessionCreationOptions() { InitialUnidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { InitialBidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { InitialDataSentLimitForPeer = validVarInt };
    }
}
