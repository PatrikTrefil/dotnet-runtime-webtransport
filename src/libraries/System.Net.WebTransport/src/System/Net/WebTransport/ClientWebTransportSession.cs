// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using System.Net.Quic;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace System.Net.WebTransport;

// TODO: use SR.PlatformNotSupported_NetWebTransport in assembly

public static class ClientWebTransportSession
{
    private const string s_extendedConnectProtocolName = "webtransport";
    private const string s_availableProtocolsHeaderName = "WT-Available-Protocols";

    /// <summary>
    /// Create a WebTransport session.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="WebTransportException">
    /// When the creation of the session fails.
    ///
    /// If the server responds with a redirect and the <see cref="WebTransportSessionCreationOptions.HttpMessageInvoker"/> does not automatically follow redirects,
    /// the method will throw a <see cref="WebTransportException"/> with error code <see cref="WebTransportError.RedirectRequired"/>.
    ///
    /// If the server responds with an unsupported maximum session count, the method will throw a <see cref="WebTransportException"/> with error code <see cref="WebTransportError.LimitExceeded"/>.
    /// When establishing a session using HTTP/3, the maximum session count values in the range [0, 65535).
    ///
    /// The method will throw a <see cref="WebTransportException"/> with error code <see cref="WebTransportError.SessionConnectFailure"/> in the following scenarios:
    /// <list type="bullet">
    /// <item>
    /// <term>The HTTP request fails.</term>
    /// <description>We do not receive a response from the server.</description>
    /// </item>
    /// <item>
    /// <term>The server does not support WebTransport over the negotiated HTTP version.</term>
    /// <description>The server does not indicate support for WebTransport during HTTP connection establishment.</description>
    /// </item>
    /// <item>
    /// <term>The server performs in invalid WebTransport handshake.</term>
    /// <description>The server does not comply with the protocol.</description>
    /// </item>
    /// <item>The server responds with a status code different from 200.</item>
    /// <description>Response with any other status code results in an exception. An exception is not thrown if the provided <see cref="WebTransportSessionCreationOptions.HttpMessageInvoker"/> automatically follows redirects.</description>
    /// <item>The maximum number of open WebTransport sessions has been reached.</item>
    /// <description>We have already opened the maximum number of open WebTransport sessions over the HTTP connection.</description>
    /// </list>
    /// </exception>
    /// <exception cref="NotSupportedException">When the combination of <see cref="WebTransportSessionCreationOptions.HttpVersion"/> and <see cref="WebTransportSessionCreationOptions.HttpVersionPolicy"/> passed in the <paramref name="options"/> is not supported.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    public static async Task<WebTransportSession> ConnectAsync(WebTransportSessionCreationOptions options, CancellationToken cancellationToken = default)
    {
        if (
            (options.HttpVersion < HttpVersion.Version30 && options.HttpVersionPolicy != HttpVersionPolicy.RequestVersionOrHigher) ||
            (options.HttpVersion > HttpVersion.Version30 && options.HttpVersionPolicy != HttpVersionPolicy.RequestVersionOrLower)
            )
        {
            throw new NotSupportedException("The requested combination of HTTP version and policy is currently not supported");
        }

        HttpRequestMessage requestMessage = new(HttpMethod.Connect, options.Uri)
        {
            Version = options.HttpVersion,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        requestMessage.Options.Set(
            Http3ExtendedConnectManager.RequestOptionsKey,
            static (Http3ExtendedConnectManagerCreationOptions options) => new MsQuicWebTransportExtendedConnectManager(options)
            );
        requestMessage.Headers.Protocol = s_extendedConnectProtocolName;
        if (options.AvailableSubProtocols != null)
        {
            requestMessage.Headers.Add(s_availableProtocolsHeaderName, options.AvailableSubProtocols);
        }

        HttpResponseMessage response;
        try
        {
            Task<HttpResponseMessage> sendTask = options.HttpMessageInvoker switch
            {
                HttpClient client => client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
                HttpMessageInvoker httpMessageInvoker => httpMessageInvoker.SendAsync(requestMessage, cancellationToken)
            };
            response = await sendTask.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new WebTransportException(WebTransportError.SessionConnectFailure, SR.net_webtransport_session_connect_failed, e);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Redirects should not be automatically followed: https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-3.3-5
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new WebTransportException(WebTransportError.RedirectRequired, SR.net_webtransport_session_connect_redirect_required, response.Headers.Location);
            }
            throw new WebTransportException(WebTransportError.SessionConnectFailure, SR.Format(SR.net_webtransport_session_connect_failed_with_status_code, (int)response.StatusCode));
        }

        Http3ExtendedConnectContent extendedConnectContent = (Http3ExtendedConnectContent)response.Content;
        MsQuicWebTransportExtendedConnectManager wtExtendedConnectManager = (MsQuicWebTransportExtendedConnectManager)extendedConnectContent.ExtendedConnectManager;

        string? selectedSubprotocol;
        try
        {
            selectedSubprotocol = GetAndValidateSelectedSubprotocolFromResponse(response, options.AvailableSubProtocols);
        }
        catch (Exception)
        {
            wtExtendedConnectManager.ReleaseSessionAfterFailedHandshake(extendedConnectContent.ConnectStream);
            throw;
        }

        WebTransportSession session = wtExtendedConnectManager.CreateSession(
            extendedConnectContent.ConnectStream,
            WrapConnectStreamBufferInArrayBuffer(extendedConnectContent.ConnectStreamBuffer),
            extendedConnectContent.QuicConnection,
            options.GracefulShutdownHandler,
            selectedSubprotocol,
            options.DefaultStreamErrorCode);

        try
        {
            await SetInitialOptionsForPeerAsync(session, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await session.CloseAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    private static System.Net.ArrayBuffer WrapConnectStreamBufferInArrayBuffer(byte[] connectStreamBufferData)
    {
        System.Net.ArrayBuffer connectStreamBuffer = new(initialSize: connectStreamBufferData.Length, usePool: true);
        if (connectStreamBufferData.Length > 0)
        {
            connectStreamBufferData.CopyTo(connectStreamBuffer.AvailableSpan);
            connectStreamBuffer.Commit(connectStreamBufferData.Length);
        }

        return connectStreamBuffer;
    }

    private static string? GetAndValidateSelectedSubprotocolFromResponse(HttpResponseMessage response, string[]? availableSubProtocols)
    {
        string? selectedSubprotocol = null;
        if (availableSubProtocols != null && response.Headers.TryGetValues("WT-Protocol", out IEnumerable<string>? values))
        {
            foreach (string value in values)
            {
                if (selectedSubprotocol != null)
                {
                    throw new WebTransportException(WebTransportError.HeaderError, SR.net_webtransport_multiple_subprotocols_selected);
                }

                try
                {
                    StructuredFieldValuesForHttp.ValidateToken(value);
                }
                catch (Exception)
                {
                    throw new WebTransportException(WebTransportError.HeaderError, SR.Format(SR.net_webtransport_server_selected_protocol_not_offered, value));
                }

                if (!availableSubProtocols.Contains(value))
                {
                    throw new WebTransportException(WebTransportError.HeaderError, SR.Format(SR.net_webtransport_server_selected_protocol_not_offered, value));
                }
                selectedSubprotocol = value;
            }
        }

        return selectedSubprotocol;
    }

    private static async Task SetInitialOptionsForPeerAsync(WebTransportSession session, WebTransportSessionCreationOptions options, CancellationToken cancellationToken)
    {
        if (options.InitialUnidirectionalStreamCountLimitForPeer > 0)
        {
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(options.InitialUnidirectionalStreamCountLimitForPeer, cancellationToken).ConfigureAwait(false);
        }
        if (options.InitialBidirectionalStreamCountLimitForPeer > 0)
        {
            await session.SetBidirectionalStreamCountLimitForPeerAsync(options.InitialBidirectionalStreamCountLimitForPeer, cancellationToken).ConfigureAwait(false);
        }
        if (options.InitialDataSentLimitForPeer > 0)
        {
            await session.SetDataSentLimitForPeerAsync(options.InitialDataSentLimitForPeer, cancellationToken).ConfigureAwait(false);
        }
    }
}
