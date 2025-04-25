using System.Net.Quic;

namespace System.Net.WebTransport;

/// <summary>
/// Demultiplexes WebTransport sessions over a single connection.
/// </summary>
public abstract class WebTransportSessionManager
{
    private readonly ConcurrentDictionary<long, Stream> _pendingUnidirectionalStreams = new();
    private readonly ConcurrentDictionary<long, Stream> _pendingBidirectionalStreams = new();
    private readonly long _maxSessions;
    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection.</param>
    /// <remarks>
    /// Hidden constructor to force instantiation through the static method <see cref="Create"/>.
    /// </remarks>
    protected WebTransportSessionManager(long maxSessions)
    {
        _maxSessions = maxSessions;
    }
    public abstract async WebTransportSession CreateSession(WebTransportSessionCreationOptions? options);
    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection.</param>
    /// <exception cref="WebTransportException">The connection is already managed by another session manager</exception>
    public static WebTransportSessionManager Create(QuicConnection connection, long maxSessions)
    {
        return new MsQuicWebTransportSessionManager(connection, maxSessions);
    }
    internal async void AddPendingStream(Stream stream, CancellationToken cancellationToken = default)
    {
        // detect type and session ID
        // add to _pendingUnidirectionalStreams or _pendingBidirectionalStreams under stream ID
    }
    internal async Stream ReceiveUnidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default) { }
    internal async Stream ReceiveBidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default) { }
}

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
internal sealed class MsQuicWebTransportSessionManager : WebTransportSessionManager
{
    private readonly QuicConnection _connection;

    /// <param name="maxSessions">The maximum number of sessions that can be created within the connection.</param>
    /// <exception cref="WebTransportException">The connection is already managed by another session manager</exception>
    internal MsQuicWebTransportSessionManager(QuicConnection connection, long maxSessions) : base(maxSessions)
    {
        _connection = connection; // TODO: check ownership of connection - there must be one session manager per connection
    }
    // TODO: single exception type for two different scenarios - those should be handled differently? -> another exception type?
    /// <exception cref="WebTransportException">Maximum number of sessions reached or the connection is already managed by another session manager</exception>
    internal override WebTransportSession CreateSession(long sessionId, WebTransportSessionCreationOptions? options)
    {
        var session = new MsQuicWebTransportSession(sessionId, this, _connection, options);
        return session;
    }
}
