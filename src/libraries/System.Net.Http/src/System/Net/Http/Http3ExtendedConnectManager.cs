// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace System.Net.Http;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal abstract class Http3ExtendedConnectManager : IDisposable
{
    /// <summary>
    /// Used to identify the <see cref="HttpRequestOptions"/> entry that contains an instance of <see cref="Http3ExtendedConnectManager"/>.
    /// </summary>
    public static readonly HttpRequestOptionsKey<Func<Action, Http3ExtendedConnectManager>> RequestOptionsKey = new("ExtendedConnectManager");

    private readonly Action _disposedCallback;
    // Detect redundant Dispose() calls in a thread-safe manner.
    // _isDisposed == 0 means Dispose(bool) has not been called yet.
    // _isDisposed == 1 means Dispose(bool) has been already called.
    private int _isDisposed;

    public Http3ExtendedConnectManager(Action disposedCallback)
    {
        _disposedCallback = disposedCallback ?? throw new ArgumentNullException(nameof(disposedCallback));
    }

    /// <summary>
    /// This method is called when the HTTP library receives a GOAWAY frame.
    /// </summary>
    public abstract Task GoAwayReceivedAsync();

    /// <summary>
    /// This method is called when the HTTP library receives a unidirectional/bidirectional QUIC stream
    /// that contains the <see cref="UnidirectionalStreamType"/> or <see cref="BidirectionalStreamSignalValue"/> as the inital bytes.
    /// </summary>
    /// <param name="streamType">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="buffer">Contains the initial part of the stream data. The buffer ownership is given to the method.</param>
    /// <param name="stream">The received stream</param>
    public abstract Task StreamReceivedAsync(QuicStreamType streamType, ArrayBuffer buffer, QuicStream stream);

    /// <summary>
    /// Variable-length integer that is sent at the start of a unidirectional HTTP/3 stream
    /// which indicates the purpose of the stream.
    /// </summary>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#unidirectional-streams"/>
    public abstract long UnidirectionalStreamType { get; }

    /// <summary>
    /// Variable-length integer that is sent at the start of a bidirectional HTTP/3 stream
    /// which indicates the purpose of the stream.
    /// </summary>
    /// <remarks>The term signal value is not directly defined in the HTTP/3 RFC, but extensions such as WebTransport refere to this value as a signal value. <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-bidirectional-streams"/></remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#name-bidirectional-streams"/>
    public abstract long BidirectionalStreamSignalValue { get; }

    /// <summary>
    /// This method is called by the HTTP library when an extended CONNECT request is being made
    /// and the protocol above HTTP/3 is expected to validate the <paramref name="serverSettings"/> (e.g. check that the server
    /// supports the used protocol).
    /// </summary>
    /// <param name="serverSettings">Server settings received in the HTTP SETTINGS frame.</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#frame-settings"/>
    public abstract void ValidateServerSettings(Dictionary<long, long> serverSettings);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            if (disposing)
            {
                _disposedCallback?.Invoke();
            }
        }
    }
}
