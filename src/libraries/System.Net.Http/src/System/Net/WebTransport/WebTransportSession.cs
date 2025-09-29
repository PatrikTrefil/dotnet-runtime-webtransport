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
// TODO: move parameter validation to the base class and keep the core methods in the derived class (is this a good idea?) If not, then CloseAsync needs a refactor
// TODO: do parameter validation first and then check state
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
    // TODO: once moved - add information to docstring that it's also used to sync access to _openStreams
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

            field = value;
        }
    }

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
            ThrowIfInvalidState();
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
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

    // TODO: move the properties implementation to the derived class and add Debug.Assert(_semaphore.CurrentCount == 0) to the setters
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
            ThrowIfInvalidState();
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
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
            ThrowIfInvalidState();

            VariableLengthIntegerValidator.ThrowIfInvalid(value);

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
        protected set
        {
            Debug.Assert(Monitor.IsEntered(_stateLock));

            field = value;
        }
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
        protected set
        {
            Debug.Assert(Monitor.IsEntered(_stateLock));

            field = value;
        }
    }

    protected Exception? GetExceptionForObjectState()
    {
        if (_isDisposed)
        {
            return new ObjectDisposedException(GetType().FullName);
        }

        WebTransportSessionState state = State;
        switch (state)
        {
            case WebTransportSessionState.ClosedLocally:
                return new InvalidOperationException("The session was closed locally.");
            case WebTransportSessionState.ClosedRemotely:
                return new WebTransportException(WebTransportError.SessionClosedByPeer, CloseStatusCode, CloseStatusDescription, "The session was closed remotely.");
            case WebTransportSessionState.AbortedLocally:
                return new WebTransportException(WebTransportError.OperationAborted, "The session was aborted because of a protocol violation by peer.");
            case WebTransportSessionState.AbortedRemotely:
                return new WebTransportException(WebTransportError.SessionClosedByPeer, CloseStatusCode, CloseStatusDescription, "The session was aborted by peer.");
        }

        Debug.Assert(state == WebTransportSessionState.Open);

        return null;
    }

    protected void ThrowIfInvalidState()
    {
        Exception? ex = GetExceptionForObjectState();

        if (ex != null)
        {
            throw ex;
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
        ThrowIfInvalidState();

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
                    if (State == WebTransportSessionState.Open)
                    {
                        State = WebTransportSessionState.ClosedLocally;
                    }
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
    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly CapsuleSender _capsuleSender;
    private Channel<ChannelItem>? _pendingUnidirectionalStreams;
    private Channel<ChannelItem>? _pendingBidirectionalStreams;
    private List<MsQuicWebTransportStream> _openStreams = [];
    private readonly ReadOnlyMemory<byte> _idEncodedAsVariableLengthInteger;
    private readonly QuicStream _connectStream;
    private readonly MsQuicWebTransportExtendedConnectManager _wtExtendedConnectManager;
    /// <summary>
    /// Used to synchronize sending of capsules using <see cref="_capsuleSender"/> and access to configuration
    /// properties for peer (<see cref="WebTransportSession.BidirectionalStreamCountLimitForPeer"/>,
    /// <see cref="WebTransportSession.UnidirectionalStreamCountLimitForPeer"/>, <see cref="WebTransportSession.DataSentLimitForPeer"/>).
    /// </summary>
    private readonly SemaphoreSlim _forPeerConfigurationSemaphore = new(1, 1);

    private bool _isDisposed { get; set; }

    /// <exception cref="ArgumentNullException">When any parameter except <paramref name="subprotocol"/> is null.</exception>
    internal MsQuicWebTransportSession(
        long id,
        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager,
        QuicStream connectStream,
        byte[] controlStreamBuffer,
        Channel<ChannelItem> pendingUnidirectionalStreams,
        Channel<ChannelItem> pendingBidirectionalStreams,
        Func<WebTransportSession, Task> gracefulShutdownHandler,
        string? subprotocol) : base(id, gracefulShutdownHandler, subprotocol)
    {
        ArgumentNullException.ThrowIfNull(wtExtendedConnectManager);
        ArgumentNullException.ThrowIfNull(connectStream);
        ArgumentNullException.ThrowIfNull(controlStreamBuffer);
        ArgumentNullException.ThrowIfNull(pendingUnidirectionalStreams);
        ArgumentNullException.ThrowIfNull(pendingBidirectionalStreams);

        _connectStream = connectStream;
        _pendingUnidirectionalStreams = pendingUnidirectionalStreams;
        _pendingBidirectionalStreams = pendingBidirectionalStreams;
        _wtExtendedConnectManager = wtExtendedConnectManager;
        _capsuleConsumer = new CapsuleConsumer(connectStream, controlStreamBuffer, this);
        _capsuleSender = new CapsuleSender(connectStream);


        if (NetEventSource.Log.IsEnabled())
        {
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
        catch (Exception) { }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream writes closed. Aborting read side...");

        lock (_stateLock)
        {
            if (State == WebTransportSessionState.Open)
            {
                MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedRemotely, null, null);
            }
        }

        // close the other side of the CONNECT stream
        _connectStream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.WebtransportSessionGone);
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
        catch (EndOfStreamException ex) // Clean termination
        {
            if (NetEventSource.Log.IsEnabled())
            {
                NetEventSource.TraceException(this, ex);
                NetEventSource.Trace(this, "CONNECT stream closed cleanly by peer. Closing session...");
            }

            // Clean termination of the CONNECT stream should be equivalent to status code 0 and description equal to an emtpy string
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-6-9
            ReceiveClose(0, "");
        }
        catch (Exception ex)
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

            lock (_stateLock)
            {
                if (State == WebTransportSessionState.Open)
                {
                    if (ex is CapsuleProtocolException)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "Invalid capsule received on CONNECT stream. Closing session...");

                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedLocally, null, null);
                    }
                    else if (ex is QuicException qex && qex.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted)
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this, "CONNECT stream closed. Closing session if not already closed...");

                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.AbortedRemotely, null, null);
                    }
                    else
                    {
                        Debug.Fail("Unexpected exception from capsule processing.");
                    }
                }
            }

            CloseOpenStreamsAndConnectStream(Http3ErrorCode.WebtransportSessionGone);
        }

        _wtExtendedConnectManager.TryRemoveSession(_connectStream);
    }

    private void MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState state, long? closeStatusCode, string? closeStatusDescription)
    {
        Debug.Assert(Monitor.IsEntered(_stateLock));
        Debug.Assert(State == WebTransportSessionState.Open);
        Debug.Assert(
            state
                is WebTransportSessionState.ClosedRemotely
                or WebTransportSessionState.ClosedLocally
                or WebTransportSessionState.AbortedLocally
                or WebTransportSessionState.AbortedRemotely
                );

        State = state;
        CloseStatusCode = closeStatusCode;
        CloseStatusDescription = closeStatusDescription;

        Exception? ex = GetExceptionForObjectState();

        Debug.Assert(ex != null);

        _pendingUnidirectionalStreams?.Writer.TryComplete(ex);
        _pendingBidirectionalStreams?.Writer.TryComplete(ex);
    }

    private async ValueTask CloseBySendingCloseCapsuleAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncStarted(this);

        lock (_stateLock)
        {
            ThrowIfInvalidState();

            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
        }

        CloseSessionCapsule closeSessionCapsule = new(closeStatus, statusDescription);

        // TODO: what if this fails? the session is already marked as closed
        await SendCapsuleAsync(
            closeSessionCapsule,
            static (session, capsule) => { },
            completeWrites: true,
            cancellationToken
            ).ConfigureAwait(false);

        _connectStream.Abort(QuicAbortDirection.Read, 0); // RFC suggests optional abort of the read side of the CONNECT stream after CLOSE_WEBTRANSPORT_SESSION

        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingCloseCapsuleAsyncCompleted(this);
    }

    public override async Task SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxUnidirectionalStreamsCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            static (session, capsule) => session.UnidirectionalStreamCountLimitForPeer = capsule.MaxUnidirectionalStreams,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    public override async Task SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);
        MaxBidirectionalStreamsCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            stateUpdate: static (session, capsule) => session.BidirectionalStreamCountLimitForPeer = capsule.MaxBidirectionalStreams,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    private async Task SendCapsuleAsync<TCapsule>(TCapsule capsule, Action<MsQuicWebTransportSession, TCapsule> stateUpdate, bool completeWrites, CancellationToken cancellationToken) where TCapsule : Capsule
    {
        await _forPeerConfigurationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _capsuleSender.SendCapsuleAsync(capsule, completeWrites, cancellationToken).ConfigureAwait(false);

            stateUpdate(this, capsule);
        }
        catch (Exception ex)
        {
            CapsuleSenderExceptionHandler(ex);
            throw;
        }
        finally
        {
            _forPeerConfigurationSemaphore.Release();
        }
    }

    public override async Task SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();
        VariableLengthIntegerValidator.ThrowIfInvalid(limit);

        MaxDataCapsule capsule = new(limit);

        await SendCapsuleAsync(
            capsule,
            static (session, capsule) => session.DataSentLimitForPeer = capsule.MaxData,
            completeWrites: false,
            cancellationToken
            ).ConfigureAwait(false);
    }

    public override async Task<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

        return await AcceptInboundStreamAsyncCore(type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreStarted(this);

        MsQuicWebTransportStream wtStream;
        try
        {
            Channel<ChannelItem>? channel = type switch
            {
                WebTransportStreamType.Unidirectional => _pendingUnidirectionalStreams,
                WebTransportStreamType.Bidirectional => _pendingBidirectionalStreams,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Invalid abort direction.")
            };

            ObjectDisposedException.ThrowIf(channel == null, this);

            ChannelItem channelItem;
            try
            {
                channelItem = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                Debug.Assert(ex.InnerException != null);
                throw ex.InnerException;
            }

            Debug.Assert(type == WebTransportStreamType.Bidirectional ? channelItem.QuicStream.CanWrite : !channelItem.QuicStream.CanWrite);
            Debug.Assert(channelItem.QuicStream.CanRead);

            wtStream = new MsQuicWebTransportStream(type, channelItem.ArrayBuffer, channelItem.QuicStream);

            await TryAddToOpenStreamsAsync(wtStream).ConfigureAwait(false);
        }
        finally
        {
            if (NetEventSource.Log.IsEnabled()) NetEventSource.AcceptInboundStreamAsyncCoreCompleted(this);
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.Associate(this, wtStream);

        return wtStream;
    }

    private async Task TryAddToOpenStreamsAsync(MsQuicWebTransportStream stream)
    {
        Exception? ex;
        lock (_stateLock)
        {
            ex = GetExceptionForObjectState();
            if (ex == null)
            {
                _openStreams.Add(stream);
            }
        }

        if (ex != null)
        {
            stream.AbortQuicStream(QuicAbortDirection.Both, Http3ErrorCode.WebtransportSessionGone);
            await stream.DisposeAsync().ConfigureAwait(false); // TODO: should we await this?
            throw ex;
        }
    }

    public override async Task<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

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
            try
            {
                QuicStream quicStream = await _wtExtendedConnectManager.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);
                wtStream = new(type, quicStream, FinishedUsingOutboundStream);
                await wtStream.InitOutbound(_idEncodedAsVariableLengthInteger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                if (ex is QuicException qex && qex.QuicError == QuicError.TransportError)
                {
                    throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error occurred.", ex);
                }
                else if (ex is OperationCanceledException or InvalidOperationException)
                {
                    throw ex;
                }
                else
                {
                    ThrowIfInvalidState();
                }

                throw;
            }

            await TryAddToOpenStreamsAsync(wtStream).ConfigureAwait(false);
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

    private void FinishedUsingOutboundStream()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        _wtExtendedConnectManager.FinishedUsingOutboundStream();
    }

    public override void Close()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        lock (_stateLock)
        {
            ThrowIfInvalidState();
            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
        }

        CloseBySendingFin();
    }

    private void CloseBySendingFin()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.CloseBySendingFinAsyncStarted(this);

        try
        {
            _connectStream.CompleteWrites();
        }
        catch (QuicException e)
        {
            throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error when closing the session.", e);
        }

        _connectStream.Abort(QuicAbortDirection.Read, 0);


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

        ThrowIfInvalidState();

        await RequestCloseAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    private async Task RequestCloseAsyncCore(CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.RequestCloseAsyncCoreStarted(this);

        try
        {
            await SendCapsuleAsync(
                DrainSessionCapsule.Instance,
                static (session, capsule) => { },
                completeWrites: false,
                cancellationToken
                ).ConfigureAwait(false);
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
            if (State != WebTransportSessionState.Open)
            {
                return;
            }
            MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedRemotely, closeStatus, statusDescription);
        }

        CloseOpenStreamsAndConnectStream(Http3ErrorCode.WebtransportSessionGone);
    }

    private void CapsuleSenderExceptionHandler(Exception ex)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, ex);

        lock (_stateLock)
        {
            ThrowIfInvalidState();

            if (ex is QuicException qex)
            {
                if (qex.QuicError == QuicError.TransportError)
                {
                    throw new WebTransportException(WebTransportError.TransportLayerError, "Transport layer error occurred.", ex);
                }
                else if (qex.QuicError == QuicError.OperationAborted)
                {
                    throw new InvalidOperationException("The session has been closed.", ex);
                }
                else if (qex.QuicError is QuicError.StreamAborted or QuicError.ConnectionAborted)
                {
                    throw new WebTransportException(WebTransportError.SessionClosedByPeer, "The session was abortively closed by peer.", ex);
                }
            }
        }
    }

    internal void CloseOpenStreamsAndConnectStream(Http3ErrorCode httpErrorCode)
    {
        lock (_stateLock)
        {
            foreach (MsQuicWebTransportStream item in _openStreams)
            {
                item.AbortQuicStream(QuicAbortDirection.Both, httpErrorCode);
            }

            _openStreams = [];
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
                List<MsQuicWebTransportStream> openStreams;
                lock (_stateLock)
                {
                    if (State == WebTransportSessionState.Open)
                    {
                        MarkSessionAsClosedAndClosePendingStreamsChannels(WebTransportSessionState.ClosedLocally, null, null);
                    }
                    openStreams = _openStreams;
                    _openStreams = [];
                }

                List<Task> closeOpenWtStreamsTasks = new();
                foreach (WebTransportStream wtStream in openStreams)
                {
                    closeOpenWtStreamsTasks.Add(wtStream.DisposeAsync().AsTask());
                }
                await Task.WhenAll(closeOpenWtStreamsTasks).ConfigureAwait(false); // these tasks should always succeed - DisposeAsync never throws

                _connectStream.Abort(QuicAbortDirection.Both, 0);

                _capsuleConsumer.Dispose();

                _pendingBidirectionalStreams = null;
                _pendingUnidirectionalStreams = null;

                _wtExtendedConnectManager.TryRemoveSession(_connectStream);
            }
        }

        await base.DisposeAsyncCore(disposing).ConfigureAwait(false);
    }
}
