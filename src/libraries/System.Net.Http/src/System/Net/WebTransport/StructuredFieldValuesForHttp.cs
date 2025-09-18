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

    /// <summary>
    /// Serialize a list of tokens.
    /// </summary>
    /// <param name="tokens">List of tokens to serialize.</param>
    /// <returns>String contaning the serialized list.</returns>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-serializing-a-list"/>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-serializing-a-token"/>
    /// <exception cref="ArgumentException">When the list serializations fails.</exception>"
    public static string SerializeListOfTokens(string[] tokens)
    {
        Debug.Assert(tokens != null);

        string result = string.Empty;
        for (int i = 0; i < tokens.Length; i++)
        {
            try
            {
                result += SerializeToken(tokens[i]);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"The token at index {i} is not valid.", nameof(tokens), e);
            }

            if (i < tokens.Length - 1)
            {
                result += ',';
            }
        }
        return result;
    }

    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-serializing-a-token"/>
    /// <exception cref="ArgumentException">When the token serialization fails.</exception>
    public static string SerializeToken(string token)
    {
        Debug.Assert(token != null);

        ValidateTokenCharacters(token);

        return token;
    }

    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-parsing-a-token"/>
    /// <exception cref="ArgumentException">When the token parsing fails.</exception>
    public static string ParseToken(string token)
    {
        Debug.Assert(token != null);

        ValidateTokenCharacters(token);

        return token;
    }

    private static void ValidateTokenCharacters(string token)
    {
        Debug.Assert(token != null);

        if (token.Length == 0)
        {
            return;
        }

        if (!char.IsAsciiLetter(token[0]) && token[0] != '*')
        {
            throw new ArgumentException("The token does not start with an ASCII letter or '*'.", nameof(token));
        }

        for (int i = 1; i < token.Length; i++)
        {
            char currChar = token[i];

            if (!IsTchar(currChar) && currChar != ':' && currChar != '/')
            {
                throw new ArgumentException($"The token contains an invalid character '{currChar}' at index {i}.", nameof(token));
            }
        }
    }

    private static bool IsTchar(char c)
    {
        return char.IsAscii(c) && (char.IsAsciiLetterOrDigit(c) || s_specialCharsThatAreNotDelimiters.Contains(c));
    }
}
