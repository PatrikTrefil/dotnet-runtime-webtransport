using System.Threading.Tasks;
using System.Threading;

using System;
using System.IO;
using System.Net.Quic;

namespace System.Net.WebTransport;

public enum WebTransportStreamState
{
    None = 0,
    /// <summary>
    /// The stream is open and can be used.
    /// </summary>
    Open,
    /// <summary>
    /// The stream is closed and can no longer be used.
    /// </summary>
    Closed
    // TODO: more states probably needed
}

// TODO: QUIC differentiates between bidirectional and unidirectional streams using a simple enum.
// TODO: how to design the api so that we can change the QUIC implementation?

// Connection-level flow control limits are not supported by System.Net.Quic

public abstract class WebTransportStream : IDisposable
{
    internal WebTransportStream(long streamId, WebTransportSession parentSession, QuicStream quicStream) {
        StreamId = streamId;
        Session = parentSession;
        QuicStream = quicStream;
    }
    internal QuicStream { get; init; }
    /// <summary>
    /// The stream ID of this stream.
    /// It is a 62-bit unsigned integer.
    /// </summary>
    long StreamId { get => QuicStream.Id }
    /// <summary>
    /// The session this stream belongs to.
    /// </summary>
    WebTransportSession Session { get; }

    WebTransportStreamState State { get; }
}

// TODO: add operations for resetting stream https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams

public interface IReadableWebTransportStream : WebTransportStream
{
    /// <summary>
    /// Reads data from the stream into the provided buffer.
    /// Returns the number of bytes read, or 0 on end-of-stream.
    /// </summary>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    Task<long> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    // TODO: what does receiveasync return?
    // TODO: how many bytes can you read at once?

    /// <summary>
    /// Abort reading early with an application-defined error code.
    /// </summary>
    void AbortReceive(long errorCode);
}


public interface IWritableWebTransportStream : WebTransportStream
{
    // TODO: note about sending the data blocked capsule https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-wt_data_blocked-capsule
    // TODO: what if the stream limit is reached?
    /// <summary>
    /// Writes data from the buffer into the stream.
    /// </summary>
    /// <exception cref="WebTransportException">When you can not send more data, because the session data limit <see cref="WebTransportSession.MaxData"/> was reached.<seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-data-limits"/> or the stream is not <see cref="WebTransportStreamState.Open"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    Task SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully end the writing side of the stream so the receiver
    /// knows no more data will arrive.
    /// </summary>
    /// <exception cref="WebTransportException">When the stream is not <see cref="WebTransportStreamState.Open"/></exception>
    /// <exception cref="OperationCanceledException">Operation cancelled</exception>
    Task CloseWriteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abort writing early with an application-defined error code.
    /// </summary>
    /// <exception cref="WebTransportException">When the stream is not <see cref="WebTransportStreamState.Open"/></exception>
    void AbortSend(long errorCode);
}

public abstract class BidirectionalWebTransportStream
    : WebTransportStream, IReadableWebTransportStream, IWritableWebTransportStream
{
    public BidirectionalWebTransportStream(long streamId, WebTransportSession parentSession, Stream quicStream): base(streamId, parentSession, quicStream) { }
}

public abstract class UnidirectionWebTransportStream: WebTransportStream, IReadableWebTransportStream
{
    public UnidirectionWebTransportStream(long streamId, WebTransportSession parentSession, Stream quicStream): base(streamId, parentSession, quicStream) { }
}
