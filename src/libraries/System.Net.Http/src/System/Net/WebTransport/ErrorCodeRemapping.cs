namespace System.Net.WebTransport;

static class ErrorCodeRemapping
{
    private const long first = 0x52e4a40fa8db;
    private const long last = 0x52e5ac983162;

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams"/>
    public static long WebTransportCodeToHttpCode(long webtransportCode)
    {
        return first + webtransportCode + Math.Floor(webtransportCode / 0x1e)
    }

    /// <summary>
    /// Convert an HTTP status code to a WebTransport status code.
    /// </summary>
    /// <param name="httpCode">The HTTP code to convert, which must be in the range [<see cref="first"/>, <see cref="last"/>] and must not be in the form '0x1f * N + 0x21'</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="ArgumentException">When the argument is in the invalid form '0x1f * N + 0x21'</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams"/>
    public static long HttpCodeToWebTransportCode(long httpCode)
    {

        if (httpCode < first || httpCode > last)
        {
            throw new ArgumentOutOfRangeException(nameof(httpCode), "must be in the range [" + first + ", " + last + "]");
        }
        if ((httpCode - 0x21) % 0x1f != 0)
        {
            throw new ArgumentException(nameof(httpCode), "must not be in the form '0x1f * N + 0x21'")
        }
        shifted = httpCode - first
        return shifted - Math.Floor(shifted / 0x1f)
    }
}
