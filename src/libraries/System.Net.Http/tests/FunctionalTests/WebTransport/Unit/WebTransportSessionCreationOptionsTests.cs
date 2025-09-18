// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Numerics;
using Xunit;

namespace System.Net.WebTransport.Unit.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class WebTransportSessionCreationOptionsTests : WebTransportTestBase
{
    private static readonly long s_maxValidVariableLengthIntegerValue = (long)BigInteger.Pow(2, 62) - 1;
    private const long s_minValidVariableLengthIntegerValue = 0;

    public static readonly TheoryData<long> s_validVariableLengthIntegers = [s_minValidVariableLengthIntegerValue, s_maxValidVariableLengthIntegerValue];
    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = [s_minValidVariableLengthIntegerValue - 1, s_maxValidVariableLengthIntegerValue + 1];

    private const char nonasciiChar = (char)129;

    // https://www.rfc-editor.org/rfc/rfc8941
    // https://datatracker.ietf.org/doc/html/rfc7230#section-3.2.6
    private static readonly char[] s_tcharDelimiterChars = ['(', ')', ',', '/', ':', ';', '<', '=', '>', '?', '@', '[', '\\', ']', '{', '}', '"'];
    private static readonly char[] s_asciiSpecialCharsThatAreNotTcharDelimiters = ['!', '#', '$', '%', '&', '\'', '*', '+', '-', '.', '^', '_', '`', '|', '~'];

    public static readonly TheoryData<string> s_invalidSubprotocols = [
        // Invalid first letter

        "1bc",
        "?bc",
        " bc",
        nonasciiChar + "bc",

        // Invalid second letter

        "bc" + nonasciiChar + "d",
        "a bc",
        ..s_tcharDelimiterChars
            .Where(c => c != '/' && c != ':') // Structured field tokens allow '/' and ':'
            .Select(d => "a" + d + "bc")
    ];


    public static readonly TheoryData<string> s_validSubprotocols = [
        "",
        "a",
        "abc",
        "*bc",
        "a1bc",
        "a:bc",
        "a/bc",
        ..s_asciiSpecialCharsThatAreNotTcharDelimiters.Select(d => "a" + d + "bc")
        ];


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

    [Theory]
    [MemberData(nameof(s_invalidSubprotocols))]
    public void InvalidProtocolThrows(string invalidSubprotocol)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                AvailableSubProtocols = [invalidSubprotocol]
            };
        });
    }

    [Theory]
    [MemberData(nameof(s_validSubprotocols))]
    public void ValidProtocolDoesNotThrow(string validSubprotocol)
    {
        new WebTransportSessionCreationOptions()
        {
            AvailableSubProtocols = [validSubprotocol]
        };
    }
}
