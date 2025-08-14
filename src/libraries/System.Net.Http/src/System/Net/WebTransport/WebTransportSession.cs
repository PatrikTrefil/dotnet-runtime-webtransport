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

    private static readonly Encoding _encoding = Encoding.UTF8;
    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;
    private readonly QuicStream _connectStream;
    private bool _isDisposed;
    private long _unidirectionalStreamCountLimitForPeer;
    private long _bidirectionalStreamCountLimitForPeer;
    private long _maxDataSentLimitForPeer;
    private readonly CancellationTokenSource _processIncomingCapsulesCancellationTokenSource = new();
    public Func<Task> GracefulShutdownHandler { get; }

    /// <exception cref="WebTransportException">When <paramref name="id"/> is not in range the range [0, 2^62).</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="controlStream"/> or <paramref name="extendedConnectManager"/> or <paramref name="controlStreamBuffer"/> is null</exception>
    internal WebTransportSession(long id, QuicStream controlStream, byte[] controlStreamBuffer, MsQuicWebTransportExtendedConnectManager extendedConnectManager, WebTransportSessionCreationOptions? options = default)
    {
        if (options == null)
        {
            options = new WebTransportSessionCreationOptions();
        }
        ArgumentNullException.ThrowIfNull(controlStream);
        ArgumentNullException.ThrowIfNull(controlStreamBuffer);
        ArgumentNullException.ThrowIfNull(extendedConnectManager);
        VariableLengthIntegerValidator.ThrowIfInvalid(id);

        Id = id;
        _capsuleConsumer = new CapsuleConsumer(controlStream, controlStreamBuffer, this);
        _capsuleSender = new CapsuleSender(controlStream);
        _connectStream = controlStream;

        SubProtocol = options.SubProtocol;
        _unidirectionalStreamCountLimitForPeer = options.InitialMaxUnidirectionalStreamCount;
        _bidirectionalStreamCountLimitForPeer = options.InitialMaxBidirectionalStreamCount;
        _maxDataSentLimitForPeer = options.InitialMaxData;
        GracefulShutdownHandler = () => options.GracefulShutdownHandler.Invoke(this);
    }

    internal void Init()
    {
        State = WebTransportSessionState.Open;
        using (ExecutionContext.SuppressFlow())
        {
            _ = ProcessIncomingCapsules(_processIncomingCapsulesCancellationTokenSource.Token);
        }
    }

    internal async Task ProcessIncomingCapsules(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _capsuleConsumer.ProcessNextCapsule(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is only triggered during session close, so no action needed.
        }
        catch (WebTransportException)
        {
            // TODO: give the exception message to the user - maybe introduce an ErrorMessage property?
            await CloseByClosingConnectStreamAsync().ConfigureAwait(false);
        }
        catch (QuicException)
        {
            State = WebTransportSessionState.Closed;
            // TODO: close open streams here, in close by sending capsule and in close connect stream
        }
        catch (Exception e)
        {
            throw e;
        }
    }

    private async ValueTask CloseBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        State = WebTransportSessionState.Closed;
        CloseStatusCode = closeStatus;
        CloseStatusDescription = _encoding.GetString(statusDescription.Span);

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);
        await _capsuleSender.SendCapsuleAsync(closeSessionCapsule, completeWrites: true, cancellationToken).ConfigureAwait(false);
        await _processIncomingCapsulesCancellationTokenSource.CancelAsync().ConfigureAwait(false);

    }
    private async ValueTask CloseByClosingConnectStreamAsync()
    {
        State = WebTransportSessionState.Closed;
        await _processIncomingCapsulesCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        await _connectStream.DisposeAsync().ConfigureAwait(false);
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

        HttpRequestMessage requestMessage = new(HttpMethod.Connect, uri)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        requestMessage.Options.Set(
            Http3ExtendedConnectManager.RequestOptionsKey,
            (Action disposedCallback) => new MsQuicWebTransportExtendedConnectManager(disposedCallback)
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
        return wtExtendedConnectManager.CreateSession(extendedConnectContent.ConnectStream, extendedConnectContent.ConnectStreamBuffer, extendedConnectContent.QuicConnection, options);
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

    public WebTransportSessionState State { get; protected set; }

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

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxUnidirectionalStreamsCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        _unidirectionalStreamCountLimitForPeer = limit;
    }

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
    /// Change of the value during the lifetime of the session is currently not supported.
    /// </summary>
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
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxBidirectionalStreamsCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        _bidirectionalStreamCountLimitForPeer = limit;
    }


    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
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

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxDataCapsule capsule = new(limit);
        await _capsuleSender.SendCapsuleAsync(capsule, completeWrites: false, cancellationToken).ConfigureAwait(false);
        _maxDataSentLimitForPeer = limit;
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
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (State != WebTransportSessionState.Open)
        {
            throw new WebTransportException("The session is not open");
        }
        await _capsuleSender.SendCapsuleAsync(DrainSessionCapsule.Instance, completeWrites: false, cancellationToken).ConfigureAwait(false);
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
    public async Task CloseAsync(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// This method should be called when the session is closed using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    internal void ReceiveClose(uint closeStatus, string statusDescription)
    {
        Debug.Assert(State == WebTransportSessionState.Open);
        CloseStatusCode = closeStatus;
        CloseStatusDescription = statusDescription;
        State = WebTransportSessionState.Closed;
    }

    /// <summary>
    /// This method should be called when the session is closed using a DRAIN_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    internal void ReceiveDrain()
    {
        Debug.Assert(State == WebTransportSessionState.Open);
        State = WebTransportSessionState.Closed;
    }

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

    protected virtual async ValueTask DisposeAsyncCore(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                await _processIncomingCapsulesCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                _processIncomingCapsulesCancellationTokenSource.Dispose();
                await _connectStream.DisposeAsync().ConfigureAwait(false);
                _capsuleConsumer.Dispose();
            }
        }
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
    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;
    private ConcurrentBag<WebTransportStream>? _openStreams = new();
    private ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;

    [MemberNotNullWhen(false, nameof(_pendingBidirectionalStreams))]
    [MemberNotNullWhen(false, nameof(_pendingUnidirectionalStreams))]
    [MemberNotNullWhen(false, nameof(_openStreams))]
    private bool _isDisposed { get; set; }

    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager,
        QuicConnection connection,
        QuicStream controlStream,
        Channel<ChannelItem> pendingUnidirectionalStreams,
        Channel<ChannelItem> pendingBidirectionalStreams,
        WebTransportSessionCreationOptions? options) : base(id, controlStream, wtExtendedConnectManager, options)
    {
        _connection = connection;
        State = WebTransportSessionState.Open;
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;

        byte[] buffer = new byte[VariableLengthIntegerHelper.MaximumEncodedLength];
        VariableLengthIntegerHelper.TryWrite(buffer, id, out int bytesWritten);
        _idEncodedAsVariableLengthInteger = buffer.AsMemory().Slice(0, bytesWritten);
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

        WebTransportStream wtStream = new MsQuicWebTransportStream(type, channelItem.ArrayBuffer, channelItem.QuicStream);
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
                List<Task> closeOpenStreamsTasks = new List<Task>();
                foreach (WebTransportStream wtStream in _openStreams)
                {
                    closeOpenStreamsTasks.Add(wtStream.DisposeAsync().AsTask());
                }
                await Task.WhenAll(closeOpenStreamsTasks).ConfigureAwait(false); // these tasks should always succeed - DisposeAsync never throws
                _openStreams = null;
            }
        }

        await base.DisposeAsyncCore(disposing).ConfigureAwait(false);
    }
}
