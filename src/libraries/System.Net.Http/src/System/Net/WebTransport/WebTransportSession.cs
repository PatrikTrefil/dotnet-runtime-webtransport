// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Diagnostics;

// TODO: separate out error messages to resx file

namespace System.Net.WebTransport;

/// <summary>
/// Represents a WebTransport session.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-1.2-3.2.1"/>
/// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12"/>
public abstract partial class WebTransportSession : IAsyncDisposable
{
    private static readonly Encoding _utf8Encoding = Encoding.UTF8;
    protected readonly Func<Task> gracefulShutdownHandler;
    protected readonly long defaultStreamErrorCode;


    /// <exception cref="WebTransportException">When <paramref name="id"/> or <paramref name="defaultStreamErrorCode"/> is not in the range [0, 2^62).</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="gracefulShutdownHandler"/> is null.</exception>
    internal WebTransportSession(long id, Func<WebTransportSession, Task> gracefulShutdownHandler,  string? subProtocol, long defaultStreamErrorCode)
    {
        ArgumentNullException.ThrowIfNull(gracefulShutdownHandler);

        VariableLengthIntegerValidator.ThrowIfInvalid(id);
        VariableLengthIntegerValidator.ThrowIfInvalid(defaultStreamErrorCode);

        Id = id;

        SubProtocol = subProtocol;
        this.gracefulShutdownHandler = () => gracefulShutdownHandler(this);
        this.defaultStreamErrorCode = defaultStreamErrorCode;
    }

    /// <summary>
    /// The identifier of the session.
    /// </summary>
    public long Id { get; }

    /// <summary>
    /// The application-layer protocol used in this session. The value is constant for the lifetime of the session.
    /// <c>null</c> indicates that no subprotocol was negotiated.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-2-9"/>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-application-protocol-negoti"/>
    public string? SubProtocol { get; }

    /// <summary>
    /// The current state of the WebTransport session.
    /// </summary>
    public abstract WebTransportSessionState State { get; protected set; }

    #region Session configuration

    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitProvidedByPeer { get; internal set; }

    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <remarks>The value may be updated using <see cref="SetUnidirectionalStreamCountLimitForPeerAsync(long, CancellationToken)"/>.</remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="UnidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="UnidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="WebTransportException">When the session's <see cref="State"/> is not <see cref="WebTransportSessionState.Open"/> or the operation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public abstract ValueTask SetUnidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitProvidedByPeer { get; internal set; }

    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <remarks>The value may be updated using <see cref="SetBidirectionalStreamCountLimitForPeerAsync(long, CancellationToken)"/>.</remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="BidirectionalStreamCountLimitForPeer"/> and send it to the peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="BidirectionalStreamCountLimitForPeer"/></param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="WebTransportException">When the session's <see cref="State"/> is not <see cref="WebTransportSessionState.Open"/> or the operation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public abstract ValueTask SetBidirectionalStreamCountLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long DataSentLimitProvidedByPeer { get; internal set; }

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <remarks>The value may be updated using <see cref="SetDataSentLimitForPeerAsync(long, CancellationToken)"/>.</remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long DataSentLimitForPeer { get; protected set; }

    /// <summary>
    /// Set a new value of <see cref="DataSentLimitForPeer"/> and send it to peer.
    /// </summary>
    /// <param name="limit">The new value for <see cref="DataSentLimitForPeer"/></param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="WebTransportException">When the session's <see cref="State"/> is not <see cref="WebTransportSessionState.Open"/> or the operation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    public abstract ValueTask SetDataSentLimitForPeerAsync(long limit, CancellationToken cancellationToken = default);

    #endregion

    /// <summary>
    /// The status code provided when closing the session.
    /// </summary>
    /// <value>
    /// </value>
    /// When the session has been closed by peer using <see cref="CloseAsync(long, string, CancellationToken)"/>,
    /// this property contains the close status code.
    /// Otherwise the value is <c>null</c>.
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public abstract long? CloseStatusCode { get; protected set; }

    /// <summary>
    /// The status description provided when closing the session.
    /// </summary>
    /// <value>
    /// When the session has been closed by peer using <see cref="CloseAsync(long, string, CancellationToken)"/>,
    /// this property contains the close status description.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// Otherwise the value is <c>null</c>.
    /// </value>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public abstract string? CloseStatusDescription { get; protected set; }

    protected WebTransportException? GetExceptionForObjectState()
    {
        WebTransportSessionState state = State;
        switch (state)
        {
            case WebTransportSessionState.ClosedLocally:
                return new WebTransportException(WebTransportError.OperationAborted, "Operation was aborted");
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
        WebTransportException? ex = GetExceptionForObjectState();

        if (ex != null)
        {
            throw ex;
        }
    }

    /// <summary>
    /// Request a graceful close of the session. The peer is expected to attempt to gracefully terminate the session as soon as possible.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    /// <exception cref="WebTransportException">When the session's <see cref="State"/> is not <see cref="WebTransportSessionState.Open"/> or the operation fails.</exception>
    public abstract ValueTask RequestCloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully close the session without providing any additional information to the peer.
    /// </summary>
    public abstract ValueTask CloseAsync();

    /// <summary>
    /// Gracefully close the session.
    /// </summary>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message will be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <remarks>The delivery of the <paramref name="closeStatus"/> and <paramref name="statusDescription"/> is best-effort.</remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.4.1"/>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="statusDescription"/> is longer than 1024 bytes after encoding.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="statusDescription"/> is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="closeStatus"/> is not in range [0, 2^32)</exception>
    public async ValueTask CloseAsync(long closeStatus, string statusDescription, CancellationToken cancellationToken = default)
    {
        if (State != WebTransportSessionState.Open)
        {
            return;
        }

        if (closeStatus < 0 || closeStatus > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(closeStatus), "The value has to be in range [0, 2^32)");
        }

        ArgumentNullException.ThrowIfNull(statusDescription);

        byte[] statusDescriptionUtf8 = _utf8Encoding.GetBytes(statusDescription);

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
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    protected abstract ValueTask CloseAsyncCore(long closeStatus, byte[] statusDescription, CancellationToken cancellationToken = default);

    /// <summary>
    /// This method should be called when peer initiates session close operation.
    /// </summary>
    /// <param name="closeStatus">Error code associated with the close operation.</param>
    /// <param name="statusDescription">Error reason associatied with the close operation.</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-10#section-4.1-2.4.1"/>
    internal abstract void ReceiveClose(uint closeStatus, string statusDescription);

    // TODO: implement throw if the maximum has been reached
    /// <summary>
    /// Creates an outbound unidirectional or bidirectional <see cref="WebTransportStream"/>.
    /// </summary>
    /// <remarks>If the transport-level connection doesn't have any available stream capacity, that is, the peer limits the concurrent stream count, the operation pends until a stream becomes available or the peer increases the stream limit.</remarks>
    /// <param name="type">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="WebTransportException">
    /// When the session's <see cref="State"/> is not <see cref="WebTransportSessionState.Open"/> or
    /// when you can not create more streams because of the peer's stream count limit has been reached
    /// (<see cref="UnidirectionalStreamCountLimitProvidedByPeer"/>, <see cref="BidirectionalStreamCountLimitProvidedByPeer"/>).
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    public async ValueTask<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

        return await OpenOutboundStreamAsyncCore(type, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="OpenOutboundStreamAsync(WebTransportStreamType, CancellationToken)"/>
    protected abstract ValueTask<WebTransportStream> OpenOutboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an inbound unidirectional or bidirectional <see cref="WebTransportStream"/>.
    /// </summary>
    /// <param name="type">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    public async ValueTask<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        ThrowIfInvalidState();

        return await AcceptInboundStreamAsyncCore(type, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AcceptInboundStreamAsync(WebTransportStreamType, CancellationToken)"/>
    protected abstract ValueTask<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, CancellationToken cancellationToken = default);

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        await CloseAsync().ConfigureAwait(false);
    }
}
