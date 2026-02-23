// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Threading;

namespace System.Net.Http;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed record class Http3ExtendedConnectManagerCreationOptions
{
    /// <summary>
    /// Call to open an outbound an outbound stream using the <see cref="QuicConnection"/> associated with the HTTP/3 connection associated with the <see cref="Http3ExtendedConnectManager"/>.
    /// </summary>
    public required Func<QuicStreamType, CancellationToken, Task<QuicStream>> OpenOutboundStreamAsync { get; init; }

    /// <summary>
    /// Call when the CONNECT stream is no longer in use.
    /// </summary>
    public required Func<QuicStream, Task> RemoveSessionAsync { get; init; }

    /// <summary>
    /// Call when the caller is finished using an outbound stream previously obtained from <see cref="OpenOutboundStreamAsync"/>.
    /// </summary>
    public required Action RemoveOutboundStream { get; init; }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public abstract class Http3ExtendedConnectManager
{
    /// <summary>
    /// Represents a factory method that creates an instance of <see cref="Http3ExtendedConnectManager"/>.
    /// </summary>
    /// <returns>A new instance of <see cref="Http3ExtendedConnectManager"/>.</returns>
    public delegate Http3ExtendedConnectManager Http3ExtendedConnectManagerValueFactory(Http3ExtendedConnectManagerCreationOptions options);

    /// <summary>
    /// Used to identify the <see cref="HttpRequestOptions"/> entry that contains an instance of <see cref="Http3ExtendedConnectManagerValueFactory"/>.
    /// </summary>
    public static readonly HttpRequestOptionsKey<Http3ExtendedConnectManagerValueFactory> RequestOptionsKey = new("ExtendedConnectManager");

    private readonly Func<QuicStreamType, CancellationToken, Task<QuicStream>> _openOutboundStreamAsyncFunc;
    private readonly Func<QuicStream, Task> _removeSessionAsyncFunc;
    private readonly Action _removeOutboundStreamFunc;

    public Http3ExtendedConnectManager(Http3ExtendedConnectManagerCreationOptions options)
    {
        Debug.Assert(options != null);

        _openOutboundStreamAsyncFunc = options.OpenOutboundStreamAsync;
        _removeSessionAsyncFunc = options.RemoveSessionAsync;
        _removeOutboundStreamFunc = options.RemoveOutboundStream;
    }

    /// <summary>
    /// This method is called when the HTTP library receives a GOAWAY frame.
    /// </summary>
    /// <remarks>
    /// This method is expected to never throw an exception.
    /// </remarks>
    public abstract Task ProcessGoAwayAsync();

    /// <summary>
    /// This method is called when the HTTP library receives a unidirectional/bidirectional QUIC stream
    /// that contains the <see cref="UnidirectionalStreamType"/> or <see cref="BidirectionalStreamSignalValue"/> as the inital bytes.
    /// </summary>
    /// <param name="streamType">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="initialData">Contains the initial part of the stream data.</param>
    /// <param name="stream">The received stream. The stream ownership is given to the method.</param>
    public abstract Task ProcessReceivedStreamAsync(QuicStreamType streamType, byte[] initialData, QuicStream stream);

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
    /// This method is called by the HTTP library when an extended CONNECT request is being made using an HTTP/3 connection
    /// and the protocol above HTTP/3 is expected to validate the <paramref name="serverSettings"/> (e.g. check that the server
    /// supports the used protocol).
    /// </summary>
    /// <remarks>
    /// It is recommended to implement caching of the validation result to avoid validating the settings multiple times.
    /// Note that this method must be thread-safe.
    /// </remarks>
    /// <param name="serverSettings">Server settings received in the HTTP SETTINGS frame.</param>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#frame-settings"/>
    public abstract void ValidateAndProcessServerSettings(Dictionary<long, long> serverSettings);

    /// <summary>
    /// This method is called by the HTTP library when an extended CONNECT request is being made using an HTTP/3 connection.
    /// It may perform validation of the request. If the request is invalid, it should throw an exception to abort the request.
    /// </summary>
    /// <remarks>Note that this method must be thread-safe.</remarks>
    public abstract void ReserveSession();

    /// <summary>
    /// This method is called by the HTTP library when an extended CONNECT request has failed.
    /// It may perform cleanup of any state associated with the request.
    /// </summary>
    /// <param name="quicStream">The stream used for the CONNECT request.</param>
    public abstract void ReleaseSessionAfterFailedHandshake(QuicStream? quicStream);

    /// <summary>
    /// Creates an outbound unidirectional or bidirectional <see cref="QuicStream"/> using
    /// the <see cref="QuicConnection"/> associated with the HTTP/3 connection that is associated with this <see cref="Http3ExtendedConnectManager"/>.
    /// </summary>
    /// <param name="type">The type of the stream, either unidirectional or bidirectional.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <exception cref="InvalidOperationException">When the underlying HTTP/3 connection has been disposed.</exception>
    protected async Task<QuicStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken)
    {
        try
        {
            return await _openOutboundStreamAsyncFunc(type, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("The HTTP/3 connection has been disposed.");
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>
    /// Call when the <paramref name="connectStream"/> is no longer in use.
    /// </summary>
    /// <param name="connectStream">The CONNECT stream that is no longer used.</param>
    protected Task RemoveSessionAsync(QuicStream connectStream) => _removeSessionAsyncFunc(connectStream);

    /// <summary>
    /// Call when the caller is finished using an outbound stream previously obtained from <see cref="OpenOutboundStreamAsync"/>.
    /// </summary>
    protected void RemoveOutboundStream() => _removeOutboundStreamFunc();
}
