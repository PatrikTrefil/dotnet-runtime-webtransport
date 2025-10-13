// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Net.Test.Common;
using Xunit;

namespace System.Net.WebTransport.Unit.Tests;

[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public class WebTransportSessionCreationOptionsTests : WebTransportTestBase
{
    public static readonly TheoryData<long> s_validVariableLengthIntegers = [VariableLengthIntegerHelper.MinValue, VariableLengthIntegerHelper.MaxValue];
    public static readonly TheoryData<long> s_invalidVariableLengthIntegers = [VariableLengthIntegerHelper.MinValue - 1, VariableLengthIntegerHelper.MaxValue + 1];

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

    private static readonly Uri s_validUri = new Uri("https://example.com");


    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialUnidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialBidirectionalStreamCountLimitForPeer = invalidVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialDataSentLimitForPeer = invalidVarInt });
    }

    [Theory]
    [MemberData(nameof(s_validVariableLengthIntegers))]
    public void ValidVariableLengthIntegerUsedToCreateInitialSessionConfigurationDoesNotThrow(long validVarInt)
    {
        new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialUnidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialBidirectionalStreamCountLimitForPeer = validVarInt };
        new WebTransportSessionCreationOptions() { Uri = s_validUri, InitialDataSentLimitForPeer = validVarInt };
    }

    [Theory]
    [MemberData(nameof(s_invalidSubprotocols))]
    public void InvalidProtocolThrows(string invalidSubprotocol)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                Uri = s_validUri,
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
            Uri = s_validUri,
            AvailableSubProtocols = [validSubprotocol]
        };
    }
}
