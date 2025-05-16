// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Net.Quic;

namespace System.Net.WebTransport;

// TODO: find out how bidirectional streams are created and is the result just a QuicStream?

public abstract class WebTransportStream : Stream, IDisposable
{
    /// <summary>
    /// The stream ID of this stream.
    /// It is a 62-bit unsigned integer.
    /// </summary>
    public abstract long StreamId { get; }
    /// <summary>
    /// The session this stream belongs to.
    /// </summary>
    public WebTransportSession Session { get; }
    /// <exception cref="ArgumentNullException">when <paramref name="parentSession"/> is null</exception>
    protected internal WebTransportStream(WebTransportSession parentSession) {
        Session = parentSession ?? throw new ArgumentNullException(nameof(parentSession));
    }

    /// <summary>
    /// Aborts either the reading, writing, or both sides of the stream.
    /// </summary>
    /// <param name="abortDirection">The direction of the stream to abort.</param>
    /// <param name="errorCode">The error code with which to abort the stream. This value is application-protocol (which is the layer above QUIC) dependent.</param>
    public abstract void Abort(WebTransportAbortDirection abortDirection, long errorCode);
}

// TODO: add session data limit tracking

/// <summary>
/// Implementation that uses System.Net.Quic
/// </summary>
internal class MsQuicWebTransportStream : WebTransportStream
{
    private readonly QuicStream _quicStream;

    public MsQuicWebTransportStream(WebTransportSession parentSession, QuicStream quicStream) : base(parentSession)
    {
        _quicStream = quicStream;
    }

    public override long StreamId => _quicStream.Id;

    public override bool CanRead => _quicStream.CanRead;

    public override bool CanSeek => _quicStream.CanSeek;

    public override bool CanWrite => _quicStream.CanWrite;

    public override long Length => _quicStream.Length;

    public override long Position { get => _quicStream.Position; set { _quicStream.Position = value; } }

    private static QuicAbortDirection WebTransportAbortDirectionToQuicAbortDirection(WebTransportAbortDirection abortDirection) => abortDirection switch
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

    public override void Flush()
    {
        _quicStream.Flush();
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        return _quicStream.Read(buffer, offset, count);
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        return _quicStream.Seek(offset, origin);
    }
    public override void SetLength(long value)
    {
        _quicStream.SetLength(value);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        _quicStream.Write(buffer, offset, count);
    }
}
