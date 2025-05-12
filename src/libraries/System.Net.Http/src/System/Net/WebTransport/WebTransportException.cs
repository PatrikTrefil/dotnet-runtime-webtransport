using System.Runtime.Serialization;

namespace System.Net.WebTransport;

public class WebTransportException : Exception
{
    public WebTransportException(string message) : base(message) { }
    public WebTransportException(string message, Exception innerException) : base(message, innerException) { }
}
