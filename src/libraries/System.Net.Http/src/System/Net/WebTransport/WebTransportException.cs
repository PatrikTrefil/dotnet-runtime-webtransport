using System.Runtime.Serialization;

namespace System.Net.WebTransport;

public class WebTransportException : Exception
{
    public WebTransportException(): base() { }
    public WebTransportException(string message): base(message) { }
    public WebTransportException(string message, Exception innerException): base(message, innerException) { }
    public WebTransportException(SerializationInfo info, StreamingContext context): base(info, context) { }
}
