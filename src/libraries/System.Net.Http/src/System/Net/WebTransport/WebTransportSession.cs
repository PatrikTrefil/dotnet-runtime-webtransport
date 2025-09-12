// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Net.Quic;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ChannelItem = (System.Net.ArrayBuffer ArrayBuffer, System.Net.Quic.QuicStream QuicStream);
using System.Collections.Generic;

namespace System.Net.WebTransport;

// TODO: maybe the public properties values should depend on _isDisposed or maybe even throw?
// TODO: implement application protocol negotiation
// TODO: add tracing/logging
// TODO: separate out error messages to resx file

public sealed record class WebTransportSessionCreationOptions
{
    // TODO: maybe the shutdown handler should have a CancellationToken parameter?
    /// <summary>
    /// This function is invoked when peer requests a graceful shutdown. The session may be used to send more data,
    /// but is should be terminated as soon as possible.
    /// </summary>
    /// <remarks>
    /// The default handler calls <see cref="WebTransportSession.Close()"/>.
    /// This handler is called when an HTTP GOAWAY frame is received or the DRAIN_WEBTRANSPORT_SESSION capsule is received.
    /// </remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#name-goaway"/>
    public Func<WebTransportSession, Task> GracefulShutdownHandler { get; init; } = (session) => { session.Close(); return Task.CompletedTask; };
    public string? SubProtocol { get; init; }
    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialUnidirectionalStreamCountLimitForPeer
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
    public long InitialBidirectionalStreamCountLimitForPeer
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
    public long InitialDataSentLimitForPeer
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
    private static readonly Encoding _encoding = Encoding.UTF8;
    private bool _isDisposed;
    /// <summary>
    /// Lock this object when working with <see cref="State"/>, <see cref="CloseStatusCode"/>,
    /// and <see cref="CloseStatusDescription"/>.
    /// </summary>
    [CLSCompliant(false)]
    protected internal readonly object _stateLock = new();

    /// <exception cref="WebTransportException">When <paramref name="id"/> is not in the range [0, 2^62).</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="gracefulShutdownHandler"/> is null.</exception>
    internal WebTransportSession(long id, Func<WebTransportSession, Task> gracefulShutdownHandler, string? subProtocol)
    {
        ArgumentNullException.ThrowIfNull(gracefulShutdownHandler);

        VariableLengthIntegerValidator.ThrowIfInvalid(id);

        Id = id;

        SubProtocol = subProtocol;
        GracefulShutdownHandler = () => gracefulShutdownHandler(this);
    }

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("osx")]
    public static bool IsSupported => QuicConnection.IsSupported;

    public Func<Task> GracefulShutdownHandler { get; }

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

    public WebTransportSessionState State
    {
        get
        {
            lock (_stateLock) { return field; }
        }
        protected set;
    }

    // TODO: add locks for configuration properties
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#streamcapacitycallback
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundunidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
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
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="UnidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="UnidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public abstract Task SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundbidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
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
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="BidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="BidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public abstract Task SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long DataSentLimitProvidedByPeer
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
    public long DataSentLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="DataSentLimitForPeer"/> and send it to peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="DataSentLimitForPeer"/></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ObjectDisposedException">When calling setter on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public abstract Task SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// When the session has been closed by peer using the CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is the "Application Error Code" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public long? CloseStatusCode
    {
        get
        {
            lock (_stateLock) { return field; }
        }
        protected set;
    }

    /// <summary>
    /// When the session has been closed by peer using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session has been closed cleanly by peer using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public string? CloseStatusDescription
    {
        get
        {
            lock (_stateLock) { return field; }
        }
        protected set;
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
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(httpMessageInvoker);

        if (uri.Scheme != "https")
        {
            throw new ArgumentException("The URI scheme must be 'https'.", nameof(uri));
        }
        options ??= new WebTransportSessionCreationOptions();

        HttpRequestMessage requestMessage = new(HttpMethod.Connect, uri)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        requestMessage.Options.Set(
            Http3ExtendedConnectManager.RequestOptionsKey,
            (Func<QuicStream, Task> finishedUsingConnectStreamCallback) => new MsQuicWebTransportExtendedConnectManager(finishedUsingConnectStreamCallback)
            );
        requestMessage.Headers.Protocol = "webtransport";

        HttpResponseMessage response;
        try
        {
            Task<HttpResponseMessage> sendTask = httpMessageInvoker is HttpClient client
                                ? client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                : httpMessageInvoker.SendAsync(requestMessage, cancellationToken);
            response = await sendTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new WebTransportException("Failed to create a WebTransport session.", e);
        }
        Http3ExtendedConnectContent extendedConnectContent = (Http3ExtendedConnectContent)response.Content;

        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager = (MsQuicWebTransportExtendedConnectManager)extendedConnectContent.ExtendedConnectManager;

        WebTransportSession session = wtExtendedConnectManager.CreateSession(
            extendedConnectContent.ConnectStream,
            extendedConnectContent.ConnectStreamBuffer,
            extendedConnectContent.QuicConnection,
            options.GracefulShutdownHandler,
            options.SubProtocol);

        if (options.InitialUnidirectionalStreamCountLimitForPeer > 0)
        {
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(options.InitialUnidirectionalStreamCountLimitForPeer, cancellationToken).ConfigureAwait(false);
        }
        if (options.InitialBidirectionalStreamCountLimitForPeer > 0)
        {
            await session.SetBidirectionalStreamCountLimitForPeerAsync(options.InitialBidirectionalStreamCountLimitForPeer, cancellationToken).ConfigureAwait(false);
        }
        if (options.InitialDataSentLimitForPeer > 0)
        {
            await session.SetDataSentLimitForPeerAsync(options.InitialDataSentLimitForPeer, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>
    /// Request a graceful close of the session. The peer is expected to attempt to gracefully terminate the session as soon as possible.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    public abstract Task RequestCloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully close the session without providing any additional information to the peer.
    /// </summary>
    public abstract void Close();

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
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        if (closeStatus < 0 || closeStatus > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(closeStatus), "The value has to be in range [0, 2^32)");
        }
        byte[] statusDescriptionUtf8 = _encoding.GetBytes(statusDescription);
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
    public abstract Task CloseAsync(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default);

    /// <summary>
    /// This method should be called when peer initiates session drain operation.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.6.1"/>
    internal void ReceiveDrain()
    {
        Debug.Assert(State == WebTransportSessionState.Open);
        GracefulShutdownHandler.Invoke();
    }

    /// <summary>
    /// This method should be called when peer initiates session close operation.
    /// </summary>
    /// <param name="closeStatus">error code associated with the close operation</param>
    /// <param name="statusDescription">error reason associatied with the close operation</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.4.1"/>
    internal abstract void ReceiveClose(uint closeStatus, string statusDescription);

    /// <summary>
    /// Creates an outbound unidirectional or bidirectional <see cref="WebTransportStream"/>.
    /// </summary>
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/> or when you can not create more streams because of the peer's unidirectional stream count limit.<seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed session.</exception>
    public abstract Task<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default);

    // TODO: maybe this should throw if the maximum has been reached?
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

    protected virtual ValueTask DisposeAsyncCore(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                lock (_stateLock)
                {
                    State = WebTransportSessionState.Closed;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore(disposing: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Implementation that uses System.Net.Quic and HTTP/3 from System.Net.Http
/// </summary>
internal sealed class MsQuicWebTransportSession : WebTransportSession
{
    private readonly QuicConnection _connection;
    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;
    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;
    private ConcurrentBag<MsQuicWebTransportStream>? _openStreams = new();
    private readonly ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;
    private readonly QuicStream _connectStream;
    private readonly MsQuicWebTransportExtendedConnectManager _wtExtendedConnectManager;
    /// <summary>
    /// WEBTRANSPORT_SESSION_GONE HTTP/3 error code.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-9.5-2.10.1"/>
    private const long s_webtransportSessionGoneErrorCode = 0x170d7b68;

    [MemberNotNullWhen(false, nameof(_pendingBidirectionalStreams))]
    [MemberNotNullWhen(false, nameof(_pendingUnidirectionalStreams))]
    [MemberNotNullWhen(false, nameof(_openStreams))]
    private bool _isDisposed { get; set; }

    /// <exception cref="ArgumentNullException">When any parameter except <paramref name="subprotocol"/> is null.</exception>
    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager,
        QuicConnection connection,
        QuicStream connectStream,
        byte[] controlStreamBuffer,
        Channel<ChannelItem> pendingUnidirectionalStreams,
        Channel<ChannelItem> pendingBidirectionalStreams,
        Func<WebTransportSession, Task> gracefulShutdownHandler,
        string? subprotocol) : base(id, gracefulShutdownHandler, subprotocol)
    {
        ArgumentNullException.ThrowIfNull(wtExtendedConnectManager);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(connectStream);
        ArgumentNullException.ThrowIfNull(controlStreamBuffer);
        ArgumentNullException.ThrowIfNull(pendingUnidirectionalStreams);
        ArgumentNullException.ThrowIfNull(pendingBidirectionalStreams);

        _connection = connection;
        _connectStream = connectStream;
        State = WebTransportSessionState.None;
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;
        _wtExtendedConnectManager = wtExtendedConnectManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, controlStreamBuffer, this);
        _capsuleSender = new CapsuleSender(connectStream);

        byte[] buffer = new byte[VariableLengthIntegerHelper.MaximumEncodedLength];
        VariableLengthIntegerHelper.TryWrite(buffer, id, out int bytesWritten);
        _idEncodedAsVariableLengthInteger = buffer.AsMemory().Slice(0, bytesWritten);
    }

    internal void Init()
    {
        lock (_stateLock)
        {
            State = WebTransportSessionState.Open;
        }

        // Reaction to peer aborting their reading of the CONNECT stream.
        _connectStream.WritesClosed.ContinueWith((_) =>
            {
                // close the other side of the CONNECT stream
                _connectStream.Abort(QuicAbortDirection.Read, s_webtransportSessionGoneErrorCode);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, // react only to peer aborting
            TaskScheduler.Current); // TODO: is current the right scheduler?

        using (ExecutionContext.SuppressFlow())
        {
            _ = ProcessIncomingCapsules();
        }
    }

    private async Task ProcessIncomingCapsules()
    {
        try
        {
            while (true)
            {
                await _capsuleConsumer.ProcessNextCapsule().ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) // Clean termination
        {
            // TODO: log the exception
            // Clean termination of the CONNECT stream should be equivalent to status code 0 and description equal to an emtpy string
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-6-9
            ReceiveClose(0, "");
        }
        catch (WebTransportException) // Unexpected capsule data received
        {
            // TODO: log the exception
            // TODO: give the exception message to the user - maybe introduce an ErrorMessage property?
            lock (_stateLock)
            {
                State = WebTransportSessionState.Closed;
            }
            _connectStream.Abort(QuicAbortDirection.Both, 0);
            if (_openStreams is not null)
            {
                foreach (MsQuicWebTransportStream item in _openStreams)
                {
                    item.AbortQuicStream(QuicAbortDirection.Both, s_webtransportSessionGoneErrorCode);
                }
            }
        }
        catch (Exception)
        {
            lock (_stateLock)
            {
                State = WebTransportSessionState.Closed;
            }
            _connectStream.Abort(QuicAbortDirection.Both, 0);
            if (_openStreams is not null)
            {
                foreach (MsQuicWebTransportStream item in _openStreams)
                {
                    item.AbortQuicStream(QuicAbortDirection.Both, s_webtransportSessionGoneErrorCode);
                }
            }
        }
        _wtExtendedConnectManager.RemoveSession(_connectStream);
    }

    private async ValueTask CloseBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            State = WebTransportSessionState.Closed;
        }

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);
        await _capsuleSender.SendCapsuleAsync(closeSessionCapsule, completeWrites: true, cancellationToken).ConfigureAwait(false);
    }

    public override async Task SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxUnidirectionalStreamsCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        UnidirectionalStreamCountLimitForPeer = limit;
    }

    public override async Task SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxBidirectionalStreamsCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        BidirectionalStreamCountLimitForPeer = limit;
    }

    public override async Task SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxDataCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        DataSentLimitForPeer = limit;
    }

    public override async Task<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }

        Channel<ChannelItem> channel = type switch
        {
            WebTransportStreamType.Unidirectional => _pendingUnidirectionalStreams,
            WebTransportStreamType.Bidirectional => _pendingBidirectionalStreams,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Invalid abort direction.")
        };
        ChannelItem channelItem = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        Debug.Assert(type == WebTransportStreamType.Bidirectional ? channelItem.QuicStream.CanWrite : !channelItem.QuicStream.CanWrite);
        Debug.Assert(channelItem.QuicStream.CanRead);

        MsQuicWebTransportStream wtStream = new MsQuicWebTransportStream(type, channelItem.ArrayBuffer, channelItem.QuicStream);
        _openStreams.Add(wtStream);
        return wtStream;

    }

    public override async Task<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        QuicStreamType quicStreamType = WebTransportStreamTypeToQuicStreamType(type);
        QuicStream quicStream = await _connection.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
        MsQuicWebTransportStream wtStream = new(type, quicStream);
        await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger).ConfigureAwait(false);
        _openStreams.Add(wtStream);
        return wtStream;
    }

    private static QuicStreamType WebTransportStreamTypeToQuicStreamType(WebTransportStreamType streamType)
    {
        return streamType switch
        {
            WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
            WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(nameof(streamType))
        };
    }

    public override void Close()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        lock (_stateLock)
        {
            State = WebTransportSessionState.Closed;
        }
        _connectStream.Abort(QuicAbortDirection.Both, 0);
    }

    public override async Task CloseAsync(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        if (statusDescription.Length > 1024)
        {
            throw new ArgumentException("The status description is longer than 1024 bytes after encoding.", nameof(statusDescription));
        }
        if (closeStatus < uint.MinValue || closeStatus > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(closeStatus), "The value has to be in range [0, 2^32)");
        }

        await CloseBySendingCloseCapsuleAsync((uint)closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public override async Task RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        await _capsuleSender.SendCapsuleAsync(DrainSessionCapsule.Instance, completeWrites: false, cancellationToken).ConfigureAwait(false);
    }

    internal override void ReceiveClose(uint closeStatus, string statusDescription)
    {
        lock (_stateLock)
        {
            Debug.Assert(State == WebTransportSessionState.Open);
            CloseStatusCode = closeStatus;
            CloseStatusDescription = statusDescription;
            State = WebTransportSessionState.Closed;
        }

        if (_openStreams is not null)
        {
            foreach (MsQuicWebTransportStream item in _openStreams)
            {
                item.AbortQuicStream(QuicAbortDirection.Both, s_webtransportSessionGoneErrorCode);
            }
        }

        _connectStream.Abort(QuicAbortDirection.Both, s_webtransportSessionGoneErrorCode);
    }

    public override Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public override Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    protected override async ValueTask DisposeAsyncCore(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                _pendingBidirectionalStreams = null;
                _pendingUnidirectionalStreams = null;
                List<Task> closeOpenStreamsTasks = new();
                foreach (WebTransportStream wtStream in _openStreams)
                {
                    closeOpenStreamsTasks.Add(wtStream.DisposeAsync().AsTask());
                }
                await Task.WhenAll(closeOpenStreamsTasks).ConfigureAwait(false); // these tasks should always succeed - DisposeAsync never throws
                _openStreams = null;
                _capsuleConsumer.Dispose();
                _wtExtendedConnectManager.RemoveSession(_connectStream);
            }
        }

        await base.DisposeAsyncCore(disposing).ConfigureAwait(false);
    }
}
