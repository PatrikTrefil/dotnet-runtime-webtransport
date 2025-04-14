using System.Threading.Tasks;
using System.Threading;

using System;

namespace System.Net.WebTransport;

public interface IWebTransportStream : IDisposable
{
    /// <summary>
    /// The stream ID of this stream.
    /// </summary>
    long StreamId { get; }
    /// <summary>
    /// The session this stream belongs to.
    /// </summary>
    IWebTransportSession Session { get; }
}

// TODO: https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-resetting-data-streams

public interface IReadableWebTransportStream : IWebTransportStream
{
    /// <summary>
    /// Reads data from the stream into the provided buffer.
    /// Returns the number of bytes read, or 0 on end-of-stream.
    /// </summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abort reading early with an application-defined error code.
    /// </summary>
    void AbortRead(long errorCode);
}

/// <seealso cref="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-data-limits"/>
public sealed class DataLimitReachedException: Exception { }

public interface IWritableWebTransportStream : IWebTransportStream
{
    /// <summary>
    /// Writes data from the buffer into the stream.
    /// </summary>
    /// <exception cref="DataLimitReachedException"
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully end the writing side of the stream so the receiver
    /// knows no more data will arrive.
    /// </summary>
    Task CloseWriteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abort writing early with an application-defined error code.
    /// </summary>
    void AbortWrite(long errorCode);
}

public interface IBidirectionalWebTransportStream
    : IReadableWebTransportStream, IWritableWebTransportStream
{
}
