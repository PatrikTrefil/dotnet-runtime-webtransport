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
using System.Runtime.CompilerServices;

// TODO: separate out error messages to resx file
// TODO: introduce invalidstate error enum option and use it when operations fail because the session was closed by this side
// TODO: move parameter validation to the base class and keep the core methods in the derived class (is this a good idea?) If not, then CloseAsync needs a refactor
// TODO: create a ClientWebTransportSession class
// TODO: create ThrowIfInvalidState method to check if the session is open and not disposed
// TODO: accept/open stream should be valuetasks because quic accept/open ops are value tasks
// TODO: the links to WT over HTTP/3 sections should be present only on the derived class. The rest should link to the WT overview doc

namespace System.Net.WebTransport;

/// <summary>
/// Represents a WebTransport session.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-1.2-3.2.1"/>
public abstract partial class WebTransportSession : IAsyncDisposable
{
    private static readonly Encoding _encoding = Encoding.UTF8;
    private bool _isDisposed;
    // TODO: move this to the derived class
    /// <summary>
    /// Lock this object when working with <see cref="State"/>, <see cref="CloseStatusCode"/>,
    /// and <see cref="CloseStatusDescription"/>.
    /// </summary>
    [CLSCompliant(false)]
    protected internal readonly object _stateLock = new();

    private static readonly Lazy<HttpMessageInvoker> s_sharedHttpMessageInvoker = new(() => new HttpClient(), true);

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

    internal Func<Task> GracefulShutdownHandler { get; }

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
        protected set
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"State transition from {field} to {value}");

            Debug.Assert(Monitor.IsEntered(_stateLock));
            // TODO: complete the writer for pending streams

            field = value;
        }
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
                throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
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
                throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
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
                throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
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
    /// <exception cref="ArgumentNullException">When <paramref name="uri"/> is null</exception>
    /// <exception cref="WebTransportException">When the creation of the session fails.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public static async Task<WebTransportSession> ConnectAsync(Uri uri, HttpMessageInvoker? httpMessageInvoker, WebTransportSessionCreationOptions? options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Scheme != "https")
        {
            throw new ArgumentException("The URI scheme must be 'https'.", nameof(uri));
        }

        httpMessageInvoker ??= s_sharedHttpMessageInvoker.Value;

        return await ConnectAsyncCore(uri, httpMessageInvoker, options ?? new WebTransportSessionCreationOptions(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WebTransportSession> ConnectAsyncCore(Uri uri, HttpMessageInvoker httpMessageInvoker, WebTransportSessionCreationOptions options, CancellationToken cancellationToken)
    {
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
        if (options.AvailableSubProtocols != null)
        {
            requestMessage.Headers.Add("WT-Available-Protocols", options.AvailableSubProtocols);
        }

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
            // TODO: handle case where user provides message invoker that does not support http/3 with special WT Error and message and write test for it
            throw new WebTransportException(WebTransportError.SessionRefused, "Failed to create a WebTransport session.", e);
        }

        Http3ExtendedConnectContent extendedConnectContent = (Http3ExtendedConnectContent)response.Content;
        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager = (MsQuicWebTransportExtendedConnectManager)extendedConnectContent.ExtendedConnectManager;

        string? selectedSubprotocol;
        try
        {
            selectedSubprotocol = GetAndValidateSelectedSubprotocolFromResponse(response, options.AvailableSubProtocols);
        }
        catch (Exception)
        {
            wtExtendedConnectManager.TryRemoveSession(extendedConnectContent.ConnectStream); // TODO: add test for this path
            throw;
        }

        WebTransportSession session = wtExtendedConnectManager.CreateSession(
            extendedConnectContent.ConnectStream,
            extendedConnectContent.ConnectStreamBuffer,
            extendedConnectContent.QuicConnection,
            options.GracefulShutdownHandler,
            selectedSubprotocol);

        await SetInitialOptions(session, options, cancellationToken).ConfigureAwait(false);

        return session;
    }

    private static string? GetAndValidateSelectedSubprotocolFromResponse(HttpResponseMessage response, string[]? availableSubProtocols)
    {
        string? selectedSubprotocol = null;
        if (availableSubProtocols != null && response.Headers.TryGetValues("WT-Protocol", out IEnumerable<string>? values))
        {
            foreach (string value in values)
            {
                if (selectedSubprotocol != null)
                {
                    throw new WebTransportException(WebTransportError.HeaderError, "Multiple WT-Protocol headers received from the server.");
                }

                try
                {
                    StructuredFieldValuesForHttp.ValidateToken(value);
                }
                catch (Exception e)
                {
                    throw new WebTransportException(WebTransportError.HeaderError, $"The server selected a protocol '{value}' that was not offered by the client.", e);
                }

                if (!availableSubProtocols.Contains(value))
                {
                    throw new WebTransportException(WebTransportError.HeaderError, $"The server selected a protocol '{value}' that was not offered by the client.");
                }
                selectedSubprotocol = value;
            }
        }

        return selectedSubprotocol;
    }

    private static async Task SetInitialOptions(WebTransportSession session, WebTransportSessionCreationOptions options, CancellationToken cancellationToken)
    {
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
    /// <exception cref="WebTransportException">When the session is not <see cref="WebTransportSessionState.Open"/> or the operation fails.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public abstract Task CloseAsync(CancellationToken cancellationToken = default);

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
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }

        if (closeStatus < 0 || closeStatus > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(closeStatus), "The value has to be in range [0, 2^32)");
        }

        byte[] statusDescriptionUtf8 = _encoding.GetBytes(statusDescription);

        if (statusDescriptionUtf8.Length > 1024)
        {
            throw new ArgumentException("The status description is longer than 1024 bytes after encoding.", nameof(statusDescription));
        }

        await CloseAsyncCore(closeStatus, statusDescriptionUtf8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully close the session.
    /// </summary>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message is expected to be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    protected abstract Task CloseAsyncCore(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default);

    /// <summary>
    /// This method should be called when peer initiates session drain operation.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.6.1"/>
    internal void ReceiveDrain()
    {
        Debug.Assert(State == WebTransportSessionState.Open);
        GracefulShutdownHandler();
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

    protected virtual ValueTask DisposeAsyncCore(bool disposing)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, $"{nameof(_isDisposed)}={_isDisposed}");

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
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

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
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;
        _wtExtendedConnectManager = wtExtendedConnectManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, controlStreamBuffer, this);
        _capsuleSender = new CapsuleSender(connectStream);


        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.Associate(this, _connection);
            NetEventSource.Associate(this, _connectStream);
            NetEventSource.Associate(this, _wtExtendedConnectManager);
            NetEventSource.Associate(this, _capsuleConsumer);
            NetEventSource.Associate(this, _capsuleSender);
        }

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

        _ = ReactToWritesClosedOnConnectStream();
        _ = ProcessIncomingCapsules();
    }

    private async Task ReactToWritesClosedOnConnectStream()
    {
        try
        {
            await _connectStream.WritesClosed.ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream writes closed. Aborting read side...");
            // close the other side of the CONNECT stream
            _connectStream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
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
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream closed cleanly by peer. Closing session...");

            // Clean termination of the CONNECT stream should be equivalent to status code 0 and description equal to an emtpy string
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-6-9
            ReceiveClose(0, "");
        }
        catch (CapsuleProtocolException) // Invalid capsule data received
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "Invalid capsule received on CONNECT stream. Closing session...");
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
                    item.AbortQuicStream(QuicAbortDirection.Both, Http3ErrorCode.WebtransportSessionGone);
                }
            }
        }
        catch (Exception)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream closed. Closing session...");

            lock (_stateLock)
            {
                State = WebTransportSessionState.Closed;
            }

            _connectStream.Abort(QuicAbortDirection.Both, 0);

            if (_openStreams is not null)
            {
                foreach (MsQuicWebTransportStream item in _openStreams)
                {
                    item.AbortQuicStream(QuicAbortDirection.Both, Http3ErrorCode.WebtransportSessionGone);
                }
            }
        }

        _wtExtendedConnectManager.TryRemoveSession(_connectStream);
    }

    private async ValueTask CloseBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncStarted(this);

        lock (_stateLock)
        {
            State = WebTransportSessionState.Closed;
        }

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);
        await _capsuleSender.SendCapsuleAsync(closeSessionCapsule, completeWrites: true, cancellationToken).ConfigureAwait(false);

        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncCompleted(this);
    }

    public override async Task SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxUnidirectionalStreamsCapsule capsule = new(limit);

        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);

        UnidirectionalStreamCountLimitForPeer = limit;
    }

    public override async Task SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxBidirectionalStreamsCapsule capsule = new(limit);

        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);

        BidirectionalStreamCountLimitForPeer = limit;
    }

    public override async Task SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxDataCapsule capsule = new(limit);

        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);

        DataSentLimitForPeer = limit;
    }

    public override async Task<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }

        return await AcceptInboundStreamAsyncCore(type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreStarted(this);

        Debug.Assert(!_isDisposed);

        MsQuicWebTransportStream wtStream;
        try
        {
            Channel<ChannelItem> channel = type switch
            {
                WebTransportStreamType.Unidirectional => _pendingUnidirectionalStreams,
                WebTransportStreamType.Bidirectional => _pendingBidirectionalStreams,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Invalid abort direction.")
            };
            ChannelItem channelItem = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            Debug.Assert(type == WebTransportStreamType.Bidirectional ? channelItem.QuicStream.CanWrite : !channelItem.QuicStream.CanWrite);
            Debug.Assert(channelItem.QuicStream.CanRead);

            wtStream = new MsQuicWebTransportStream(type, channelItem.ArrayBuffer, channelItem.QuicStream);
            _openStreams.Add(wtStream);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreCompleted(this);
        }
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    public override async Task<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }
        QuicStreamType quicStreamType = WebTransportStreamTypeToQuicStreamType(type);

        return await OpenOutboundStreamAsyncCore(type, quicStreamType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebTransportStream> OpenOutboundStreamAsyncCore(WebTransportStreamType type, QuicStreamType quicStreamType, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.OpenOutboundStreamCoreStarted(this);

        Debug.Assert(!_isDisposed);

        MsQuicWebTransportStream wtStream;
        try
        {
            QuicStream quicStream = await _connection.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
            wtStream = new(type, quicStream);
            await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger).ConfigureAwait(false);
            _openStreams.Add(wtStream);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.OpenOutboundStreamCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private static QuicStreamType WebTransportStreamTypeToQuicStreamType(WebTransportStreamType streamType, [CallerArgumentExpression(nameof(streamType))] string? paramName = null)
    {
        return streamType switch
        {
            WebTransportStreamType.Unidirectional => QuicStreamType.Unidirectional,
            WebTransportStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(paramName, streamType, "Invalid stream type.")
        };
    }

    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }

        lock (_stateLock)
        {
            State = WebTransportSessionState.Closed;
        }

        await CloseBySendingFinAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseBySendingFinAsync(CancellationToken cancellationToken)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncStarted(this);

        try
        {
            await _connectStream.WriteAsync(ReadOnlyMemory<byte>.Empty, completeWrites: true, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException e)
        {
            throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error when closing the session.", e);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncCompleted(this);
    }

    protected override async Task CloseAsyncCore(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        await CloseBySendingCloseCapsuleAsync((uint)closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public override async Task RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException(WebTransportError.SessionClosed, CloseStatusCode, CloseStatusDescription, "The session is not open");
        }

        await RequestCloseAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    private async Task RequestCloseAsyncCore(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.RequestCloseAsyncCoreStarted(this);

        try
        {
            await _capsuleSender.SendCapsuleAsync(DrainSessionCapsule.Instance, completeWrites: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.RequestCloseAsyncCoreCompleted(this);
        }
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

        CloseOpenStreamsAndConnectStream(Http3ErrorCode.WebtransportSessionGone);
    }

    internal void CloseOpenStreamsAndConnectStream(Http3ErrorCode httpErrorCode)
    {
        if (_openStreams is not null)
        {
            foreach (MsQuicWebTransportStream item in _openStreams)
            {
                item.AbortQuicStream(QuicAbortDirection.Both, httpErrorCode);
            }
        }

        _connectStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
    }

    protected override async ValueTask DisposeAsyncCore(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                List<Task> closeOpenWtStreamsTasks = new();
                foreach (WebTransportStream wtStream in _openStreams)
                {
                    closeOpenWtStreamsTasks.Add(wtStream.DisposeAsync().AsTask());
                }
                await Task.WhenAll(closeOpenWtStreamsTasks).ConfigureAwait(false); // these tasks should always succeed - DisposeAsync never throws

                _connectStream.Abort(QuicAbortDirection.Both, 0);

                _capsuleConsumer.Dispose();

                _pendingBidirectionalStreams = null;
                _pendingUnidirectionalStreams = null;
                _openStreams = null;

                _wtExtendedConnectManager.TryRemoveSession(_connectStream);
            }
        }

        await base.DisposeAsyncCore(disposing).ConfigureAwait(false);
    }
}
