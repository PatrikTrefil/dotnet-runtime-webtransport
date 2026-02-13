// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Net.WebTransport;

internal static class ErrorCodeRemapping
{
    private const long first = 0x52e4a40fa8db;
    private const long last = 0x52e5ac983162;

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams"/>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="webtransportCode"/> is not in the range [0, 2^32)</exception>
    public static long WebTransportCodeToHttpCode(long webtransportCode, [CallerArgumentExpression(nameof(webtransportCode))] string? paramName = null)
    {
        if (webtransportCode < 0 || webtransportCode > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, "must be in the range [0, 2^32)");
        }

        long longWebtransportCode = webtransportCode;
        return first + longWebtransportCode + (longWebtransportCode / 0x1e);
    }

    /// <summary>
    /// Convert an HTTP status code to a WebTransport status code.
    /// </summary>
    /// <param name="httpCode">The HTTP code to convert, which must be in the range [<see cref="first"/>, <see cref="last"/>] and must not be in the form '0x1f * N + 0x21'</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="httpCode"/> is not in range [0x52e4a40fa8db, 0x52e5ac983162]</exception>
    /// <exception cref="ArgumentException">When the argument is in the invalid form '0x1f * N + 0x21'</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams"/>
    public static long HttpCodeToWebTransportCode(long httpCode)
    {

        if (httpCode < first || httpCode > last)
        {
            throw new ArgumentOutOfRangeException(nameof(httpCode), "must be in the range [" + first + ", " + last + "]");
        }
        if ((httpCode - 0x21) % 0x1f == 0)
        {
            throw new ArgumentException(nameof(httpCode), "must not be in the form '0x1f * N + 0x21'");
        }
        long shifted = httpCode - first;
        return shifted - (shifted / 0x1f);
    }
}
