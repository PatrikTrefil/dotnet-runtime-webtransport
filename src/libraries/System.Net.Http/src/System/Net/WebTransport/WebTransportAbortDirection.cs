namespace System.Net.WebTransport;

/// <summary>
/// Specifies the direction of the <see cref="WebTransportStream"/> which is to be aborted.
/// This enumeration supports a bitwise combination of its member values.
/// </summary>
/// <seealso cref="WebTransportStream.Abort(System.Net.WebTransport.WebTransportAbortDirection, long)"/>
[Flags]
public enum WebTransportAbortDirection
{
    Read = 1,
    Write = 2,
    Both = 3
}
