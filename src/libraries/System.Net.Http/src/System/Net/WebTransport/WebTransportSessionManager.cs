using System.Net.Quic;

namespace System.Net.WebTransport;

// TODO: I can not create multiple sessions over a single http connection - I don't have access to the Http3Connection object

/// <summary>
/// Demultiplexes WebTransport sessions over a single connection.
/// </summary>
public abstract class WebTransportSessionManager
{
    private readonly ConcurrentDictionary<long, Stream> _pendingUnidirectionalStreams = new();
    private readonly ConcurrentDictionary<long, Stream> _pendingBidirectionalStreams = new();
    private readonly long? _maxSessions;
    private readonly HttpClient _httpClient;
    private readonly Uri _uri;
    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection.</param>
    /// <remarks>
    /// Hidden constructor to force instantiation through the static method <see cref="Create"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">The uri is not a valid WebTransport URI</exception>
    protected WebTransportSessionManager(Uri uri, HttpClient httpClient, long? maxSessions)
    {
        _maxSessions = maxSessions;
        _httpClient = httpClient;
        _uri = uri;
    }
    public abstract async Task<WebTransportSession> CreateSessionAsync(WebTransportSessionCreationOptions? options);
    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection. Setting this limit only makes sense on the server side.</param>
    /// <exception cref="ArgumentException">The uri is not a valid WebTransport URI</exception>
    /// <exception cref="WebTransportException">The connection is already managed by another session manager</exception>
    public static WebTransportSessionManager Create(Uri uri, HttpClient httpClient, long? maxSessions)
    {
        return new MsQuicWebTransportSessionManager(uri, httpClient, maxSessions);
    }

    internal async void AddPendingStream(Stream stream, CancellationToken cancellationToken = default)
    {
        // detect type and session ID
        // add to _pendingUnidirectionalStreams or _pendingBidirectionalStreams under stream ID
    }
    internal async Task<Stream> ReceiveUnidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default) { }
    internal async Task<Stream> ReceiveBidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default) { }
}

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
internal sealed class MsQuicWebTransportSessionManager : WebTransportSessionManager
{
    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection.</param>
    /// <exception cref="WebTransportException">The connection is already managed by another session manager</exception>
    /// <exception cref="ArgumentException">The uri is not a valid WebTransport URI</exception>
    internal MsQuicWebTransportSessionManager(Uri uri, HttpClient httpClient, long? maxSessions) : base(uri, httpClient, maxSessions) { }

    /// <exception cref="WebTransportException">Maximum number of sessions reached or specified <see cref="WebTransportSessionCreationOptions.SubProtocol"/> is not supported</exception>
    internal override async Task<WebTransportSession> CreateSessionAsync(WebTransportSessionCreationOptions? options)
    {
        HttpRequestMessage requestMessage = new(HttpMethod.Connect, uri) { Version = HttpVersion.Version30 };
        HttpResponseMessage response = await httpClient.SendAsync(requestMessage).ConfigureAwait(false);
        Stream connectedStream = response.Content.ReadAsStream();
        Debug.Assert(connectedStream.CanWrite);
        Debug.Assert(connectedStream.CanRead);
        QuicConnection connection; // TODO: can't get the connection from HttpClient
        MsQuicWebTransportSession session = new(sessionId, this, connection, options);
        return session;
    }
}
