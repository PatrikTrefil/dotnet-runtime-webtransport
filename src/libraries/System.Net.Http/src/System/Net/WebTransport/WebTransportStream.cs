using System.Threading.Tasks;
using System.Threading;

using System;
using System.IO;
using System.Net.Quic;

namespace System.Net.WebTransport;

// TODO: implementation = forward all Stream abstradt methods to the private _stream, but with locking
public abstract class WebTransportStream : Stream, IDisposable
{
    public WebTransportStream(WebTransportSession parentSession, Stream stream) {
        Session = parentSession;
        _stream = stream;
    }

    /// <summary>
    /// Used to lock <see cref="_stream"/>
    /// </summary>
    private readonly object _streamLock = new();
    private readonly Stream _stream;
    /// <summary>
    /// The stream ID of this stream.
    /// It is a 62-bit unsigned integer.
    /// </summary>
    public abstract long StreamId { get; }
    /// <summary>
    /// The session this stream belongs to.
    /// </summary>
    public WebTransportSession Session { get; }

    // API copied from https://learn.microsoft.com/en-us/dotnet/api/system.net.quic.quicstream.abort?view=net-9.0#system-net-quic-quicstream-abort(system-net-quic-quicabortdirection-system-int64)
    /// <summary>
    /// Aborts either the reading, writing, or both sides of the stream.
    /// </summary>
    /// <param name="abortDirection">The direction of the stream to abort.</param>
    /// <param name="errorCode">The error code with which to abort the stream. This value is application-protocol (which is the layer above QUIC) dependent.</param>
    public abstract void Abort(QuicAbortDirection abortDirection, long errorCode);

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
public class MsQuicWebTransportStream: WebTransportStream
{
    private readonly QuicStream _quicStream;

    public MsQuicWebTransportStream(WebTransportSession parentSession, QuicStream quicStream): base(parentSession, quicStream)
    {
        _quicStream; = quicStream;
    }

    public override long StreamId => QuicStream.Id;

    public override void Abort(QuicAbortDirection abortDirection, long errorCode)
    {
        _quicStream.Abort(abortDirection, errorCode);
    }
}
