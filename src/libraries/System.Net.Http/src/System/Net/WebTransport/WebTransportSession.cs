using System;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;

namespace System.Net.WebTransport;

// TODO: https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-use-of-keying-material-expo
// TODO: maybe use IAsyncDisposable instead of IDisposable everywhere?

public enum WebTransportState
{
    None = 0;
    /// <summary>
    /// The connection is negotiating the handshake with the remote endpoint.
    /// </summary>
    Connecting;

    /// <summary>
    /// The initial state after the HTTP handshake has been completed.
    /// </summary>
    Open;

    /// <summary>
    /// A close message was sent to the remote endpoint.
    /// </summary>
    CloseSent;

    /// <summary>
    /// A close message was received from the remote endpoint.
    /// </summary>
    CloseReceived;

    /// <summary>
    /// Indicates the session close handshake completed gracefully.
    /// </summary>
    Closed;

    /// <summary>
    /// Indicates that the session has been aborted.
    /// </summary>
    Aborted;
}

public record class WebTransportSessionCreationOptions
{
    // TODO: add validation in initializers
    public string? SubProtocol { get; init; }
    public Priority InitialPriority { get; init; } = Priority.Default();
    /// <summary>
    /// Default value is zero, which indicates no support for datagrams.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <seealso cref="https://www.rfc-editor.org/rfc/rfc9221#name-transport-parameter"/>
    public long InitalMaxDatagramSize { get; init; } = 0;
    /// <summary>
    /// Default value is zero
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialMaxUnidirectionalStreamCount { get; init; } = 0;
    /// <summary>
    /// Default value is zero
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
    public long InitialMaxBidirectionalStreamCount { get; init; } = 0;
    /// <summary>
    /// Default value is zero
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_DATA"/>
    public long InitialMaxData { get; init; } = 0;
}

public abstract class WebTransportSession : IDisposable
{
    public WebTransportSession(long id, WebTransportSessionCreationOptions? options) {
        Id = id;

        SubProtoconitl = options.SubProtocol;
        Priority = options.InitialPriority;
        MaxDatagramSize = options.InitalMaxDatagramSize;
        MaxUnidirectionalStreamCount = options.InitialMaxUnidirectionalStreamCount;
        MaxBidirectionalStreamCount = options.InitialMaxBidirectionalStreamCount;
        MaxData = options.InitialMaxData;
    }

    /// <summary>
    /// The identifier of the session. The identifier is the same as the identifier of the CONNECT stream that initiated the session.
    /// The value is constant for the lifetime of the session. It is a 62-bit unsigned integer.
    /// </summary>
    public long Id { get; }
    /// <summary>
    /// The application-layer protocol used in this session. The value is constant for the lifetime of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-application-protocol-negoti"/>
    public string? SubProtocol { get; }

    // QUIC priority support https://github.com/dotnet/runtime/issues/90281
    /// <summary>
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-prioritization"/>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    public Priority Priority { get; set; }

    public WebTransportState State { get; private set; }

    // TODO: I think the max datagram size is actually a property of the underlying QUIC connection and should be removed from this class.
    /// <summary>
    /// The maximum size of a datagram that can be sent within the session, in units of bytes.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/>
    public long MaxDatagramSize { get; set; }

    // TODO: add validation in setters for config properties
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#streamcapacitycallback
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundunidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long UnidirectionalStreamCountLimitProvidedByPeer { get; }
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long UnidirectionalStreamCountLimitForPeer { get; set; }

    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundbidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long BidirectionalStreamCountLimitProvidedByPeer { get; }
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long BidirectionalStreamCountLimitForPeer { get; set; }

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    long MaxDataSentLimitProvidedByPeer { get; }
    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    long MaxDataSentLimitForPeer { get; set; }

    // TODO: consider replacing with dictionary where keys are ids
    // TODO: is this even useful? The client may want to find out how many streams were created within this session to check if it can create more
    IReadOnlyList<WebTransportStream> OpenStreams { get; }

    /// <summary>
    /// When the session has been closed by a CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is the "Application Error Code" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    uint? CloseStatusCode { get; private set; }

    /// <summary>
    /// If the session has been closed using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    string? CloseStatusDescription { get; private set; }

    /// <summary>
    /// Initiate a graceful close of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async void CloseAsync(CancellationToken cancellationToken = default);

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
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async void CloseAsync(int closeStatus, string statusDescription, CancellationToken cancellationToken = default);
    // TODO: use this in the implementation UTF8Encoding utf8WithException = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Create a unidirectional stream. The calling side can write, and the remote can only read.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's unidirectional stream count limit.<seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async WebTransportStream CreateUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's bidirectional stream count limit</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async WebTransportStream CreateBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a unidirectional stream. The initiator can write, and the receiver can read.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async WebTransportStream ReceiveUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async WebTransportStream ReceiveBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a datagram message (unreliable/unordered). Length is limited by <see cref="MaxDatagramSize" />.
    /// </summary>
    /// <exception cref="WebTransportException">When the datagram is larger than the maximum allowed datagram size.<seealso cref="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async void SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    // TODO: this should probably return number of bytes received or something like that
    // TODO: maybe this should be an event handler? https://github.com/dotnet/runtime/issues/53533
    /// <summary>
    /// Receive the next datagram, if any. Typically also returns the length or
    /// can place data into a buffer.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    abstract async void ReceiveDatagramAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}

public sealed class MsQuicWebTransportSession: WebTransportSession
{
    private readonly QuicConnection _connection;
    public MsQuicWebTransportSession(long id, QuicConnection connection, WebTransportSessionCreationOptions? options): base(id, options)
    {
        _connection = connection;
    }
    public override async WebTransportStream ReceiveUnidirectionalStreamAsync(CancellationToken cancellationToken = default)
    {
        var stream = await _connection.AcceptInboundStreamAsync(cancellationToken);
        // TODO: write the stream type and session id
        return new MsQuicWebTransportStream(this, stream);
    }
}
