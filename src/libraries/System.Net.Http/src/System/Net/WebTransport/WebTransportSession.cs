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


public readonly record struct Priority(byte urgency, bool incremental)
{
    public static Priority Default() => new Priority(3, false);
    private byte _urgency = urgency;
    /// <summary>
    /// The value is an unsigned integer in the range [0-7].
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the provided value is out of range.</exception>
    public byte Urgency {
        get => _urgency;
        init {
            if (value > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(Urgency), value, "Value msut be in the range [0-7]")
            }
            _urgency = value;
        }
    }
    /// <summary>
    /// If true, indicates that the data can be processed incrementally.
    /// </summary>
    public bool Incremental { get; init; } = incremental;
}

public record class WebTransportSessionCreationOptions
{
    // TODO: add validation in setters
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

// TODO: who can change settings? both parties? do i need two properties for every thing?

public abstract class WebTransportSession : IDisposable
{
    internal WebTransportSession(long id, WebTransportSessionCreationOptions? options) {
        Id = id;

        SubProtocol = options.SubProtocol;
        Priority = options.InitialPriority;
        MaxDatagramSize = options.InitalMaxDatagramSize;
        MaxUnidirectionalStreamCount = options.InitialMaxUnidirectionalStreamCount;
        MaxBidirectionalStreamCount = options.InitialMaxBidirectionalStreamCount;
        MaxData = options.InitialMaxData;
    }

    /// <summary>
    /// The identifier of the session. The identifier is the same as the identifier of the QUIC stream that initiated the session.
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
    /// <exception cref="WebTransportException">When trying to set priority, but the session is not <see cref="WebTransportState.Open"/>.</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    public Priority Priority { get; set; }

    public WebTransportState State { get; }

    // TODO: add validation in setters
    /// <summary>
    /// The maximum size of a datagram that can be sent within the session, in units of bytes.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/>
    public long MaxDatagramSize { get; set; }
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long MaxUnidirectionalStreamCount { get; set; }
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is larger then 2^60</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    long MaxBidirectionalStreamCount { get; set; }
    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    long MaxData { get; set; }
    // TODO: consider replacing with dictionary where keys are ids
    IReadOnlyList<IWebTransportStream> OpenStreams { get; }

    /// <summary>
    /// When the session has been closed by a CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is the "Application Error Code" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    uint? CloseStatusCode { get; }

    /// <summary>
    /// If the session has been closed using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    string? CloseStatusDescription { get; }

    /// <summary>
    /// Initiate a graceful close of the session.
    /// </summary>
    /// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
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
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task CloseAsync(int closeStatus, string statusDescription, CancellationToken cancellationToken = default)
    {
        // UTF8Encoding utf8WithException = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    // TODO: how to include optimistic opening of streams? https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-webtransport-features
    /// <summary>
    /// Create a unidirectional stream. The calling side can write, and the remote can only read.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's unidirectional stream count limit.<seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task<IUnidirectionalWebTransportStream> CreateUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's bidirectional stream count limit</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task<IBidirectionalWebTransportStream> CreateBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a unidirectional stream. The initiator can write, and the receiver can read.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task<IUnidirectionalWebTransportStream> ReceiveUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task<IBidirectionalWebTransportStream> ReceiveBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a datagram message (unreliable/unordered). Length is limited by <see cref="MaxDatagramSize" />.
    /// </summary>
    /// <exception cref="WebTransportException">When the datagram is larger than the maximum allowed datagram size.<seealso cref="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive the next datagram, if any. Typically also returns the length or
    /// can place data into a buffer. An alternative is a TryReceive pattern.
    /// </summary>
    /// <returns>Number of bytes received (0 if none) and possibly an end-of-session flag.</returns>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    Task<ValueWebTransportDatagramReceiveResult> ReceiveDatagramAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}
