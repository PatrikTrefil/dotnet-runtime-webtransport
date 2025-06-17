// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Quic;
using System.Numerics;

namespace System.Net.WebTransport;


public sealed record class WebTransportSessionCreationOptions
{
    // TODO: maybe the shutdown handler should have a CancellationToken parameter?
    /// <summary>
    /// This function is invoked when peer requests a graceful shutdown. The session may be used to send more data,
    /// but is should be terminated as soon as possible.
    /// </summary>
    /// <remarks>
    /// The default handler calls <see cref="WebTransportSession.CloseAsync(long, string, CancellationToken)"/> with status code 0 and an empty message.
    /// This handler is called when an HTTP GOAWAY frame is received or the DRAIN_WEBTRANSPORT_SESSION capsule is received.
    /// </remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#name-goaway"/>
    public Func<WebTransportSession, Task> GracefulShutdownHandler { get; init; } = (session) => session.CloseAsync(0, "");
    public string? SubProtocol { get; init; }
    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialMaxUnidirectionalStreamCount
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }

    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
    public long InitialMaxBidirectionalStreamCount
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }

    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_DATA"/>
    public long InitialMaxData
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }
}

/// <summary>
/// Represents a WebTransport session.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-1.2-3.2.1"/>
public abstract partial class WebTransportSession : IAsyncDisposable
{
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("osx")]
    public static bool IsSupported => QuicConnection.IsSupported;
    private readonly CapsuleConsumer _capsuleConsumer;
    private long _unidirectionalStreamCountLimitForPeer;
    private long _bidirectionalStreamCountLimitForPeer;
    private long _maxDataSentLimitForPeer;
    /// <exception cref="WebTransportException">When <paramref name="id"/> is not in range the range [0, 2^62).</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="controlStream"/> or <paramref name="extendedConnectManager"/> or <paramref name="options"/> is null</exception>
    internal WebTransportSession(long id, Stream controlStream, MsQuicWebTransportExtendedConnectManager extendedConnectManager, WebTransportSessionCreationOptions? options = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(controlStream);
        ArgumentNullException.ThrowIfNull(sessionManager);
        Id = id;
        _capsuleConsumer = new CapsuleConsumer(controlStream, this);
        _controlStream = controlStream;
        this.sessionManager = sessionManager;

        SubProtocol = options.SubProtocol;
        UnidirectionalStreamCountLimitForPeer = options.InitialMaxUnidirectionalStreamCount;
        BidirectionalStreamCountLimitForPeer = options.InitialMaxBidirectionalStreamCount;
        MaxDataSentLimitForPeer = options.InitialMaxData;
    }

    internal void Init()
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = ProcessIncomingCapsules();
        }
        State = WebTransportSessionState.Open;
    }
    internal async Task ProcessIncomingCapsules()
    {
        while (true)
        {
            await _capsuleConsumer.ProcessNextCapsule().ConfigureAwait(false);
            // TODO: handle WebTransportExceptions and QuicExceptions?
        }
    }
    /// <summary>
    /// Create a WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="uri"/>  does not use https scheme</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="uri"/> or <paramref name="httpMessageInvoker"/> is null</exception>
    /// <exception cref="WebTransportException">When the creation of the session fails.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public static async Task<WebTransportSession> ConnectAsync(Uri uri, HttpMessageInvoker? httpMessageInvoker, WebTransportSessionCreationOptions? options = default, CancellationToken cancellationToken = default)
    {
        WebTransportSessionManager sessionManager = WebTransportSessionManager.Create(uri, httpClient);
        return await sessionManager.CreateSessionAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// The identifier of the session. The identifier is the same as the identifier of the CONNECT stream that initiated the session.
    /// The value is constant for the lifetime of the session. It is a 62-bit unsigned integer.
    /// </summary>
    public long Id { get; }
    /// <summary>
    /// The application-layer protocol used in this session. The value is constant for the lifetime of the session.
    /// <c>null</c> indicates that no subprotocol was negotiated.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-application-protocol-negoti"/>
    public string? SubProtocol { get; }
    protected WebTransportSessionManager sessionManager { get; }

    public WebTransportSessionState State { get; protected set; }

    // TODO: add validation in setters for config properties
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#streamcapacitycallback
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundunidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitProvidedByPeer
    {
        get;
        internal set
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            if (State != WebTransportSessionState.Open)
            {
                throw new WebTransportException("The session is not open");
            }
            field = value;
        }
    }

    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// Change of the value during the lifetime of the session is currently not supported.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitForPeer => _unidirectionalStreamCountLimitForPeer;

    /// <summary>
    /// Set a new value of <see cref="UnidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="UnidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public async Task SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
    }

    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundbidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitProvidedByPeer
    {
        get;
        internal set
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            if (State != WebTransportSessionState.Open)
            {
                throw new WebTransportException("The session is not open");
            }
            field = value;
        }
    }

    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// Change of the value during the lifetime of the session is currently not supported.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="ObjectDisposedException">When calling getter on a disposed session.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitForPeer => _bidirectionalStreamCountLimitForPeer;

    /// <summary>
    /// Set a new value of <see cref="BidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="BidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public async Task SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    }


    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long MaxDataSentLimitProvidedByPeer
    {
        get;
        internal set
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            if (State != WebTransportSessionState.Open)
            {
                throw new WebTransportException("The session is not open");
            }
            field = value;
        }
    }

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long MaxDataSentLimitForPeer => _maxDataSentLimitForPeer;

    /// <summary>
    /// Set a new value of <see cref="MaxDataSentLimitForPeer"/> and send it to peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="MaxDataSentLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public async Task SetMaxDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
    }

    /// <summary>
    /// When the session has been closed by a CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is the "Application Error Code" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public long? CloseStatusCode { get; private set; }

    /// <summary>
    /// If the session has been closed using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public string? CloseStatusDescription { get; private set; }

    /// <summary>
    /// Request a graceful close of the session. The peer is expected to attempt to gracefully terminate the session as soon as possible.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    public async Task RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        DrainSessionCapsule drainSessionCapsule = new();
        drainSessionCapsule.Serialize(_controlStream);
        await _controlStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully close the session.
    /// </summary>
    /// <remarks>
    /// The session is closed using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </remarks>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message will be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="statusDescription"/> is longer than 1024 bytes after encoding.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="statusDescription"/> is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="closeStatus"/> is not in range [0, 2^32)</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    public async Task CloseAsync(long closeStatus, string statusDescription, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> statusDescriptionUtf8 = _encoding.GetBytes(statusDescription);
        await CloseAsync(closeStatus, statusDescriptionUtf8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully close the session.
    /// </summary>
    /// <remarks>
    /// The session is closed using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </remarks>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message is expected to be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="statusDescription"/> is longer than 1024 bytes.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="closeStatus"/> is not in range [0, 2^32)</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    public async Task CloseAsync(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
    {
        if (statusDescription.Length > 1024)
        {
            throw new ArgumentException("The status description is longer than 1024 bytes after encoding.", nameof(statusDescription));
        }
        CloseSessionCapsule closeSessionCapsule = new(this, closeStatus, statusDescription);
        await closeSessionCapsule.SerializeAsync(_controlStream, cancellationToken).ConfigureAwait(false);
        await _controlStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// This method should be called when the session is closed using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    internal void ReceiveClose(uint closeStatus, string statusDescription)
    {
        CloseStatusCode = closeStatus;
        CloseStatusDescription = statusDescription;
    }

    /// <summary>
    /// This method should be called when the session is closed using a DRAIN_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    internal void ReceiveDrain()
    {
    }

    /// <summary>
    /// Creates an outbound unidirectional or bidirectional <see cref="WebTransportStream"/>.
    /// </summary>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/> or when you can not create more streams because of the peer's unidirectional stream count limit.<seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    public abstract Task<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an inbound unidirectional or bidirectional <see cref="WebTransportStream"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    public abstract Task<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a datagram message (unreliable/unordered). Length is limited by maximum datagram size of the underlying transport.
    /// </summary>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/> or when the datagram is larger than the maximum datagram size of the underlying transport.<seealso href="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    public abstract Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive the next datagram (unreliable/unordered).
    /// </summary>
    /// <returns>The total number of bytes read into buffer between zero and min(<paramref name="buffer"/>.Length, maximum datagram size of the underlying transport]</returns>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="buffer"/> is null</exception>
    public abstract Task<int> ReceiveDatagramAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            // TODO: set large fields to null
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
internal sealed class MsQuicWebTransportSession : WebTransportSession
{
    private readonly QuicConnection _connection;
    private readonly QuicStream _controlStream;
    private readonly MsQuicWebTransportServerSessionManager _msQuicSessionManager;
    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportServerSessionManager sessionManager,
        QuicConnection connection,
        QuicStream controlStream) : this(id, sessionManager, connection, controlStream, new WebTransportSessionCreationOptions()) { }
    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportServerSessionManager sessionManager,
        QuicConnection connection,
        QuicStream controlStream,
        WebTransportSessionCreationOptions options) : base(id, controlStream, sessionManager, options)
    {
        _connection = connection;
        _controlStream = controlStream;
        _msQuicSessionManager = sessionManager;
    }
    public override async Task<WebTransportStream> ReceiveUnidirectionalStreamAsync(CancellationToken cancellationToken = default)
    {
        QuicStream stream = await _msQuicSessionManager.ReceiveUnidirectionalStreamAsync(Id, cancellationToken).ConfigureAwait(false);
        return new MsQuicWebTransportStream(this, stream);
    }
    public override async Task<WebTransportStream> ReceiveBidirectionalStreamAsync(CancellationToken cancellationToken = default)
    {
        QuicStream stream = await _msQuicSessionManager.ReceiveBidirectionalStreamAsync(Id, cancellationToken).ConfigureAwait(false);
        return new MsQuicWebTransportStream(this, stream);
    }

    public override Task<WebTransportStream> CreateUnidirectionalStreamAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public override Task<WebTransportStream> CreateBidirectionalStreamAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public override Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public override Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
