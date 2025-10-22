// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Net.WebTransport;

/// <summary>
/// Implements parts of "Structured Field Values for HTTP" (RFC 8941).
/// </summary>
/// <seealso href="https://www.rfc-editor.org/rfc/rfc8941"/>
internal static class StructuredFieldValuesForHttp
{
    private static readonly char[] s_specialCharsThatAreNotDelimiters = ['!', '#', '$', '%', '&', '\'', '*', '+', '-', '.', '^', '_', '`', '|', '~'];

    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-serializing-a-token"/>
    /// <exception cref="ArgumentException">When the token serialization fails.</exception>
    public static void ValidateToken(string token)
    {
        Debug.Assert(token != null);

        if (token.Length == 0)
        {
            return;
        }

        if (!char.IsAsciiLetter(token[0]) && token[0] != '*')
        {
            throw new ArgumentException(SR.net_webtransport_invalid_starting_char_in_token, nameof(token));
        }

        for (int i = 1; i < token.Length; i++)
        {
            char currChar = token[i];

            if (!IsTchar(currChar) && currChar != ':' && currChar != '/')
            {
                throw new ArgumentException(SR.Format(SR.net_webtransport_invalid_char_in_token, currChar, i), nameof(token));
            }
        }
    }

    private static bool IsTchar(char c)
    {
        return char.IsAscii(c) && (char.IsAsciiLetterOrDigit(c) || s_specialCharsThatAreNotDelimiters.Contains(c));
    }
}
