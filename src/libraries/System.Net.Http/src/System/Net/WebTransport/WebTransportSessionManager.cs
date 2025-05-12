using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Diagnostics;

namespace System.Net.WebTransport;

// TODO: there is no guarantee that the user can create multiple sessions over a single connection

// A WebTransport application may be a client or a server or both.
// TODO: revise the class hierarchy


/// <summary>
/// Demultiplexes WebTransport sessions over a single connection.
/// </summary>
public abstract class WebTransportSessionManager
{
    protected const long UnidirectionalStreamType = 0x54;
    protected internal WebTransportSessionManager() { }
    /// <exception cref="WebTransportException">When a new session cannot be created, because the GOAWAY frame was received or the concurrent sessions limit has been reached.</exception>
    public abstract Task<WebTransportSession> CreateSessionAsync(WebTransportSessionCreationOptions? options, CancellationToken cancellationToken = default);
    /// <summary>
    /// Gracefully closes all sessions managed by this session manager by closing the underlying connection.
    /// </summary>
    public abstract Task CloseAll();
    internal abstract Task GoAwayReceivedAsync();
}

public abstract class WebTransportServerSessionManager : WebTransportSessionManager
{
    protected readonly long maxSessions;
    protected internal WebTransportServerSessionManager(long maxSessions) : base()
    {
        this.maxSessions = maxSessions;
    }
    public static WebTransportSessionManager CreateServerManager(long maxSessions = 0)
    {
        return new MsQuicWebTransportServerSessionManager(maxSessions);
    }
    public abstract Task<WebTransportSession> ReceiveSessionAsync(CancellationToken cancellationToken = default);
}
public abstract class WebTransportClientSessionManager : WebTransportSessionManager
{
    internal WebTransportClientSessionManager() : base() { }
}

internal sealed class MsQuicWebTransportServerSessionManager : WebTransportServerSessionManager
{
    // TODO: provide a robust implementation of the producer-consumer pattern
    private readonly ConcurrentDictionary<long, ConcurrentBag<QuicStream>> _pendingUnidirectionalStreams = new();
    private readonly ConcurrentDictionary<long, ConcurrentBag<QuicStream>> _pendingBidirectionalStreams = new();
    internal MsQuicWebTransportServerSessionManager(long maxSessions) : base(maxSessions) { }

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
    internal async Task<QuicStream> ReceiveUnidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default)
    {
    }
    internal async Task<QuicStream> ReceiveBidirectionalStreamAsync(long sessionId, CancellationToken cancellationToken = default) { }


    internal async void AddPendingUnidirectionalStream(QuicStream stream, CancellationToken cancellationToken = default)
    {
        long streamType = VariableLengthIntegerStreamHelper.Read(stream);
        long sessionId = VariableLengthIntegerStreamHelper.Read(stream);
        if (streamType != UnidirectionalStreamType)
        {
            throw new WebTransportException($"Invalid stream type {streamType}");
        }
        ConcurrentBag<QuicStream> bag = _pendingUnidirectionalStreams.GetOrAdd(sessionId, sessionId => new ConcurrentBag<QuicStream>());
        bag.Add(stream);
    }
    internal async void AddPendingBidirectionalStream()
    {

        // TODO: this will have to tightly integrate with http 3
    }

    public override Task CloseAll() => throw new NotImplementedException();
    public override Task<WebTransportSession> CreateSessionAsync(WebTransportSessionCreationOptions? options, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public override Task<WebTransportSession> ReceiveSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class MsQuicWebTransportClientSessionManager : WebTransportClientSessionManager
{
    public override Task CloseAll() => throw new NotImplementedException();
    public override Task<WebTransportSession> CreateSessionAsync(WebTransportSessionCreationOptions? options, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
