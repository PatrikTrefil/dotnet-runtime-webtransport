using System.Threading.Tasks;
using System.Threading;

using System;
using System.IO;
using System.Net.Quic;

namespace System.Net.WebTransport;

// TODO: implementation = forward all Stream abstradt methods to the private _stream with limit counting
public abstract class WebTransportStream : Stream, IDisposable
{
    public WebTransportStream(WebTransportSession parentSession, Stream stream)
    {
        Session = parentSession;
        _stream = stream;
    }

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
    public abstract void Abort(WebTransportAbortDirection abortDirection, long errorCode);

    /// <summary>
    /// Implementation that uses System.Net.Quic
    /// </summary>
    internal class MsQuicWebTransportStream : WebTransportStream
    {
        private readonly QuicStream _quicStream;

        public MsQuicWebTransportStream(WebTransportSession parentSession, QuicStream quicStream) : base(parentSession, quicStream)
        {
            _quicStream; = quicStream;
        }

        public override long StreamId => QuicStream.Id;

        private WebTransportAbortDirectionToQuicAbortDirection(WebTransportAbortDirection abortDirection) => abortDirection switch
        {
            WebTransportAbortDirection.Read => QuicAbortDirection.Read,
            WebTransportAbortDirection.Write => QuicAbortDirection.Write,
            WebTransportAbortDirection.Both => QuicAbortDirection.Both,
            _ => throw new ArgumentOutOfRangeException(nameof(abortDirection), abortDirection, "Invalid abort direction.")
        };
        public override void Abort(WebTransportAbortDirection abortDirection, long errorCode)
        {
            QuicAbortDirection quicAbortDirection = WebTransportAbortDirectionToQuicAbortDirection(abortDirection);
            _quicStream.Abort(quicAbortDirection, errorCode);
        }
    }
