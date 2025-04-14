using System;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Generic;

namespace System.Net.WebTransport;

// TODO: find the implementation of variable length enconding in QUIC
// TODO: https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-use-of-keying-material-expo
// TODO: note about sending the data blocked capsule https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_data_blocked-capsule

public enum WebTransportState
{
    /// <summary>
    /// The connection is negotiating the handshake with the remote endpoint.
    /// </summary>
    Connecting = 1;

    /// <summary>
    /// The initial state after the HTTP handshake has been completed.
    /// </summary>
    Open = 2;

    /// <summary>
    /// A close message was sent to the remote endpoint.
    /// </summary>
    CloseSent = 3;

    /// <summary>
    /// A close message was received from the remote endpoint.
    /// </summary>
    CloseReceived = 4;

    /// <summary>
    /// Indicates the session close handshake completed gracefully.
    /// </summary>
    Closed = 5;

    /// <summary>
    /// Indicates that the session has been aborted.
    /// </summary>
    Aborted = 6;
}


// This interface has been copied from WebSocketContext
public interface IWebTransportContext
{
    Uri RequestUri { get; }
    string Origin { get; }
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-prioritization"/>
    NameValueCollection Headers { get; }
    IEnumerable<string> SubProtocols { get; }
    CookieCollection CookieCollection { get; }
    IPrincipal? User { get; }
    bool IsAuthenticated { get; }
    bool IsLocal { get; }
    bool IsSecureConnection { get; }
    IWebTransportSession WebTransportSession { get; }
}

public interface Priority
{
    /// <summary>
    /// The value is an unsigned integer in the range 0-7.
    /// </summary>
    byte Urgency { get; }
    /// <summary>
    /// If true, indicates that the data can be processed incrementally.
    /// </summary>
    bool Incremental { get; }
}

// These two exception may be thrown by session constructors
/// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-interaction-with-http-3-goa"/>
public sealed class GoAwayReceivedException: Exception { }
/// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-interaction-with-http-3-goa"/>
public sealed class WebTransportSessionDrainReceivedException : Exception { }
/// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-simu"/>
public sealed class SessionCountLimitOverSingleHttpConnectionReachedException: Exception { }
/// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" />
public sealed class StreamCountLimitReached: Exception { }


// TODO: who can change settings? both parties?
public interface IWebTransportSession : IDisposable
{
    /// <summary>
    /// The identifier of the session. The identifier is the same as the identifier of the QUIC stream that initiated the session.
    /// The value is constant for the lifetime of the session.
    /// </summary>
    ulong Id { get; }
    /// <summary>
    /// The application-layer protocol used in this session. The value is constant for the lifetime of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-application-protocol-negoti"/>
    string? SubProtocol { get; }

    // QUIC priority support https://github.com/dotnet/runtime/issues/90281
    /// <summary>
    /// The value may be changed by the session initiator during the lifetime of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-prioritization"/>
    Priority Priority { get; set; }

    WebTransportState State { get; }

    ulong MaxDatagramSize { get; set; } = 0
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session. This value cannot exceed 2^60.
    /// The value may be changed by the session initiator during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    ulong MaxUnidirectionalStreamCount { get; set; }
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session. This value cannot exceed 2^60.
    /// The value may be changed by the session initiator during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    ulong MaxBidirectionalStreamCount { get; set; }
    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes.
    /// The value must be greater then 0.
    /// The value may be changed by the session initiator during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    ulong MaxData { get; }
    // TODO: consider replacing with dictionary where keys are ids
    IReadOnlyList<IWebTransportStream> OpenStreams { get; }

    /// <summary>
    /// When the session is closed cleanly, the value will be 0.
    /// When the session is closed by a CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is provided by the remote endpoint.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    int? CloseStatusDescription { get; }

    /// <summary>
    /// If the session has been closed using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the error message provided by the remote endpoint in the capsule.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    string? CloseStatusDescription { get; }

    /// <summary>
    /// Initiate a graceful close of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Close the session using using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message will be encoded to UTF-8.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the statusDescription is longer than 1024 bytes after encoding.</exception> 
    Task CloseAsync(int closeStatus, string statusDescription, CancellationToken cancellationToken = default)
    {
        // UTF8Encoding utf8WithException = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    // TODO: how to include optimistic opening of streams? https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-webtransport-features
    /// <summary>
    /// Create a unidirectional stream. The calling side can write, and the remote can only read.
    /// </summary>
    /// <exception cref="StreamCountLimitReached">When you can not create more streams because of the peer's unidirectional stream count limit</exception>
    Task<IUnidirectionalWebTransportStream> CreateUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="StreamCountLimitReached">When you can not create more streams because of the peer's bidirectional stream count limit</exception>
    Task<IBidirectionalWebTransportStream> CreateBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept a unidirectional stream. The initiator can write, and the receiver can read.
    /// </summary>
    Task<IUnidirectionalWebTransportStream> AcceptUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept a bidirectional stream. Both ends can read and write.
    /// </summary>
    Task<IBidirectionalWebTransportStream> AcceptBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a datagram message (unreliable/unordered). Length is limited by <see cref="MaxDatagramSize" />.
    /// </summary>
    Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive the next datagram, if any. Typically also returns the length or
    /// can place data into a buffer. An alternative is a TryReceive pattern.
    /// </summary>
    /// <returns>Number of bytes received (0 if none) and possibly an end-of-session flag.</returns>
    Task<ValueWebTransportDatagramReceiveResult> ReceiveDatagramAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}
