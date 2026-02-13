// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Net.WebTransport;

internal static class ThrowHelper
{
    private const long s_maxOpenQuicStreamsPerType = 65534;

    /// <summary>
    /// Validates the stream count limit is supported by MsQuic.
    /// </summary>
    /// <seealso href="https://microsoft.github.io/msquic/msquicdocs/docs/Streams.html#stream-id-flow-control"/>
    /// <exception cref="ArgumentOutOfRangeException">When the values is not in the range [0, 65535).</exception>
    internal static void ValidateStreamCountLimit(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < 0 || value > s_maxOpenQuicStreamsPerType)
        {
            throw new ArgumentOutOfRangeException(paramName, SR.Format(SR.net_webtransport_invalid_stream_count_limit, value));
        }
    }

    /// <summary>
    /// Validates the session count limit is supported by MsQuic.
    /// </summary>
    /// <seealso href="https://microsoft.github.io/msquic/msquicdocs/docs/Streams.html#stream-id-flow-control"/>
    /// <exception cref="WebTransportException">When the values is not in the range (0, 65535).</exception>
    internal static void ValidateSessionCountLimit(long value)
    {
        if (value == 0)
        {
            throw new WebTransportException(WebTransportError.HeaderError, SR.net_webtransport_server_does_not_support_webtransport_over_http3);
        }

        if (value > s_maxOpenQuicStreamsPerType)
        {
            throw new WebTransportException(WebTransportError.HeaderError, SR.Format(SR.net_webtransport_unsupported_maximum_session_count, value));
        }
    }
}
