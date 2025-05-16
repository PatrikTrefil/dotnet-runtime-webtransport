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

// TODO: https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-use-of-keying-material-expo
// TODO: maybe use IAsyncDisposable an addition to IDisposable everywhere?
// TODO: add throw ArgumentNullException for all properties that are not nullable in all files
// TODO: who manages the connection settings - i.e. SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI , etc.?

public enum WebTransportSessionState
{
    None = 0,
    Connecting,
    Open,
    Closed,
    Aborted
}

internal static class VariableLengthIntegerValidator
{
    private const long MaxValue = (1L << 62) - 1;
    public static void ThrowIfInvalid(long value)
    {
        if (value is < 0 or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value must be in the range [0, 2^62)");
        }
    }
}

public sealed record class WebTransportSessionCreationOptions
{
    private readonly long _initialMaxUnidirectionalStreamCount;
    private readonly long _initialMaxBidirectionalStreamCount;
    private readonly long _initialMaxData;
    public string? SubProtocol { get; init; }
    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialMaxUnidirectionalStreamCount
    {
        get => _initialMaxUnidirectionalStreamCount;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            _initialMaxUnidirectionalStreamCount = value;
        }
    }
    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
    public long InitialMaxBidirectionalStreamCount
    {
        get => _initialMaxBidirectionalStreamCount; init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            _initialMaxBidirectionalStreamCount = value;
        }
    }
    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_DATA"/>
    public long InitialMaxData
    {
        get => _initialMaxData;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value); _initialMaxData = value;
        }
    }
}

public abstract class WebTransportSession : IDisposable
{
    private static readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly CapsuleConsumer _capsuleConsumer;
    private readonly Stream _controlStream;
    private bool _disposedValue;

    /// <exception cref="ArgumentNullException">When <paramref name="controlStream"/> or <paramref name="sessionManager"/> is null</exception>
    protected internal WebTransportSession(long id, Stream controlStream, WebTransportSessionManager sessionManager) : this(id, controlStream, sessionManager, new WebTransportSessionCreationOptions()) { }
    /// <exception cref="ArgumentNullException">When <paramref name="controlStream"/> or <paramref name="sessionManager"/> or <paramref name="options"/> is null</exception>
    protected internal WebTransportSession(long id, Stream controlStream, WebTransportSessionManager sessionManager, WebTransportSessionCreationOptions options)
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
    /// Create a WebTransport session. If you need to create multiple sessions over a single HTTP/3 connection, use <see cref="WebTransportSessionManager.Create"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The uri is not a valid WebTransport URI</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="uri"/> or <paramref name="httpClient"/> is null</exception>
    public static async Task<WebTransportSession> Create(Uri uri, HttpClient httpClient, WebTransportSessionCreationOptions? options)
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
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-application-protocol-negoti"/>
    public string? SubProtocol { get; }
    protected WebTransportSessionManager sessionManager { get; }

    public WebTransportSessionState State { get; private set; }

    // TODO: add validation in setters for config properties
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#streamcapacitycallback
    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundunidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitProvidedByPeer { get; internal set; }
    /// <summary>
    /// A count of the cumulative number of unidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// Change of the value during the lifetime of the session is currently not supported.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long UnidirectionalStreamCountLimitForPeer { get; set; }

    // TODO: use https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-options#maxinboundbidirectionalstreams
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitProvidedByPeer { get; internal set; }
    /// <summary>
    /// A count of the cumulative number of bidirectional streams that can be opened
    /// over the lifetime of the session by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// Change of the value during the lifetime of the session is currently not supported.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_streams-capsule"/>
    public long BidirectionalStreamCountLimitForPeer { get; set; }

    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by this endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long MaxDataSentLimitProvidedByPeer { get; internal set; }
    /// <summary>
    /// The maximum amount of data that can be sent on the entire session, in units of bytes, by the remote endpoint.
    /// The value must be in the range [0, 2^62).
    /// The value may be changed during the lifetime of the session.
    /// The stream header is excluded from this limit so that this limit does not prevent the sending
    /// of information that is essential in linking new streams to a specific WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is equal or greater than 2^62</exception>
    /// <exception cref="ObjectDisposedException">When calling setter on a closed session.</exception>
    /// <exception cref="WebTransportException">When calling the setter, but the session is not <see cref="WebTransportSessionState.Open"/>.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_max_data-capsule"/>
    public long MaxDataSentLimitForPeer { get; set; }

    /// <summary>
    /// When the session has been closed by a CLOSE_WEBTRANSPORT_SESSION capsule, the
    /// value is the "Application Error Code" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public uint? CloseStatusCode { get; private set; }

    /// <summary>
    /// If the session has been closed using the CLOSE_WEBTRANSPORT_SESSION capsule,
    /// this property contains the "Application Error Message" part of the capsule.
    /// When the session is closed cleanly using a GOAWAY frame or DRAIN_WEBTRANSPORT_SESSION, the value is null.
    /// The description may be up to 1024 bytes long in UTF-8 encoding.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    public string? CloseStatusDescription { get; private set; }

    /// <summary>
    /// Initiate a graceful close of the session.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a closed session.</exception>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        DrainSessionCapsule drainSessionCapsule = new();
        drainSessionCapsule.Serialize(_controlStream);
        await _controlStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Close the session using using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message will be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="statusDescription"/> is longer than 1024 bytes after encoding.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="statusDescription"/> is null</exception>
    public async void CloseAsync(uint closeStatus, string statusDescription, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> statusDescriptionUtf8 = _encoding.GetBytes(statusDescription);
        await CloseAsync(closeStatus, statusDescriptionUtf8, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Close the session using using a CLOSE_WEBTRANSPORT_SESSION capsule.
    /// </summary>
    /// <param name="closeStatus">Reason code sent in the capsule.</param>
    /// <param name="statusDescription">
    /// Message sent in the capsule. The message is expected to be encoded to UTF-8 without BOM.
    /// The maximum length of the message after the encoding is 1024 bytes.
    /// If null is provided, the message will be empty.
    /// </param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-session-termination"/>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="statusDescription"/> is longer than 1024 bytes.</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    public async Task CloseAsync(uint closeStatus, ReadOnlyMemory<byte> statusDescription, CancellationToken cancellationToken = default)
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
    /// Create a unidirectional stream. The calling side can write, and the remote can only read.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's unidirectional stream count limit.<seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-limiting-the-number-of-stre" /></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    public abstract Task<WebTransportStream> CreateUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not create more streams because of the peer's bidirectional stream count limit</exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    public abstract Task<WebTransportStream> CreateBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a unidirectional stream. The initiator can write, and the receiver can read.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    public abstract Task<WebTransportStream> ReceiveUnidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive a bidirectional stream. Both ends can read and write.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    public abstract Task<WebTransportStream> ReceiveBidirectionalStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a datagram message (unreliable/unordered). Length is limited by maximum datagram size of the underlying transport.
    /// </summary>
    /// <exception cref="WebTransportException">When the datagram is larger than the maximum datagram size of the underlying transport.<seealso href="https://datatracker.ietf.org/doc/html/rfc9221#name-transport-parameter"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null</exception>
    public abstract Task SendDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive the next datagram (unreliable/unordered).
    /// </summary>
    /// <returns>The total number of bytes read into buffer between zero and min(<paramref name="buffer"/>.Length, maximum datagram size of the underlying transport]</returns>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    /// <exception cref="ObjectDisposedException">When calling method on a session in <see cref="WebTransportSessionState.Closed"/> state.</exception>
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
