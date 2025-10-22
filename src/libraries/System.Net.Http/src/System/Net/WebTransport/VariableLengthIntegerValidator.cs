// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Net.WebTransport;

internal static class VariableLengthIntegerValidator
{
    private const long MaxValue = (1L << 62) - 1;
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    public static void ThrowIfInvalid(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is < 0 or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, SR.net_webtransport_invalid_variable_length_integer);
        }
    }
}
