// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Net.Http;
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
    private static readonly long s_validVarInt = 42;
    private static readonly HttpMessageInvoker s_validHttpMessageInvoker = new HttpClient();


    [Theory]
    [MemberData(nameof(s_invalidVariableLengthIntegers))]
    public void InvalidVariableLengthIntegerUsedToCreateInitialSessionConfigurationThrows(long invalidVarInt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialUnidirectionalStreamCountLimitForPeer = invalidVarInt, DefaultStreamErrorCode = s_validVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialBidirectionalStreamCountLimitForPeer = invalidVarInt, DefaultStreamErrorCode = s_validVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialDataSentLimitForPeer = invalidVarInt, DefaultStreamErrorCode = s_validVarInt });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialDataSentLimitForPeer = s_validVarInt, DefaultStreamErrorCode = invalidVarInt });
    }

    [Theory]
    [MemberData(nameof(s_validVariableLengthIntegers))]
    public void ValidVariableLengthIntegerUsedToCreateInitialSessionConfigurationDoesNotThrow(long validVarInt)
    {
        new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialUnidirectionalStreamCountLimitForPeer = validVarInt, DefaultStreamErrorCode = validVarInt };
        new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialBidirectionalStreamCountLimitForPeer = validVarInt, DefaultStreamErrorCode = validVarInt };
        new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialDataSentLimitForPeer = validVarInt, DefaultStreamErrorCode = validVarInt };
        new WebTransportSessionCreationOptions() { HttpMessageInvoker = s_validHttpMessageInvoker, Uri = s_validUri, InitialDataSentLimitForPeer = validVarInt, DefaultStreamErrorCode = validVarInt };
    }

    [Theory]
    [MemberData(nameof(s_invalidSubprotocols))]
    public void InvalidProtocolThrows(string invalidSubprotocol)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            WebTransportSessionCreationOptions options = new()
            {
                HttpMessageInvoker = s_validHttpMessageInvoker,
                Uri = s_validUri,
                AvailableSubProtocols = [invalidSubprotocol],
                DefaultStreamErrorCode = s_validVarInt
            };
        });
    }

    [Theory]
    [MemberData(nameof(s_validSubprotocols))]
    public void ValidProtocolDoesNotThrow(string validSubprotocol)
    {
        new WebTransportSessionCreationOptions()
        {
            HttpMessageInvoker = s_validHttpMessageInvoker,
            Uri = s_validUri,
            AvailableSubProtocols = [validSubprotocol],
            DefaultStreamErrorCode = s_validVarInt
        };
    }

    [Fact]
    public void SettingUriToNullThrows()
    {
        Assert.Throws<ArgumentNullException>("value", () => new WebTransportSessionCreationOptions()
        {
            HttpMessageInvoker = s_validHttpMessageInvoker,
            Uri = null,
            DefaultStreamErrorCode = s_validVarInt
        });
    }

    [Fact]
    public void SettingShutdownHandlerToNullThrows()
    {
        Assert.Throws<ArgumentNullException>("value", () => new WebTransportSessionCreationOptions()
        {
            HttpMessageInvoker = s_validHttpMessageInvoker,
            Uri = s_validUri,
            GracefulShutdownHandler = null,
            DefaultStreamErrorCode = s_validVarInt
        });
    }

    [Fact]
    public void UsingNonHttpsUriThrows()
    {
        Assert.Throws<ArgumentException>(() => new WebTransportSessionCreationOptions()
        {
            HttpMessageInvoker = s_validHttpMessageInvoker,
            Uri = new Uri("http://example.com"),
            DefaultStreamErrorCode = s_validVarInt
        });
    }

    [Fact]
    public void UsingRelativeUriThrows()
    {
        Uri relativeUri = new("/a/b/c", UriKind.Relative);
        Assert.Throws<ArgumentException>(() => new WebTransportSessionCreationOptions()
        {
            HttpMessageInvoker = s_validHttpMessageInvoker,
            Uri = relativeUri,
            DefaultStreamErrorCode = s_validVarInt
        });
    }
}
