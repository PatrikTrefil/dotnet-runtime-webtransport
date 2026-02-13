// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport;

internal interface IMsQuicWebTransportSessionConnectionManager
{
    /// <summary>
    /// Creates an outbound unidirectional or bidirectional <see cref="QuicStream"/> using
    /// the <see cref="QuicConnection"/> associated with the HTTP/3 connection that is associated with this <see cref="IMsQuicWebTransportSessionConnectionManager"/>.
    /// </summary>
    /// <param name="type">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="InvalidOperationException">When the underlying HTTP/3 connection has been disposed.</exception>
    Task<QuicStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken);
    /// <summary>
    /// Call when the caller is finished using an outbound stream previously obtained from <see cref="OpenOutboundStreamAsync"/>.
    /// </summary>
    void RemoveOutboundStream();
    /// <summary>
    /// Call when a session is closed and the CONNECT stream is no longer used.
    /// This method may be called multiple times for the same stream and is thread-safe.
    /// </summary>
    /// <param name="connectStream">CONNECT stream of the session to remove.</param>
    void RemoveSession(QuicStream connectStream);
}
