// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using System.Net.Quic;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace System.Net.WebTransport;

public static class ClientWebTransportSession
{
    private static readonly Lazy<HttpMessageInvoker> s_sharedHttpMessageInvoker = new(() => new HttpClient(), true);

    // TODO: IsSupported property is not for all WebTransport but only for WT over HTTP/3 - how to reflect this?
    // TODO: we also need a property to check for support of WT over HTTP/3 on the server side as well (analogous to QuicListener.IsSupported)
    /// <summary>
    /// Gets a value that indicates whether WebTransport is supported for client scenarios on the current machine.
    /// </summary>
    /// <value>
    /// <c>true</c> if <see cref="QuicConnection.IsSupported"/> returns true; otherwise, <c>false</c>.
    /// </value>
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("osx")]
    public static bool IsSupported => QuicConnection.IsSupported;

    /// <summary>
    /// Create a WebTransport session using HTTP/3.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="WebTransportException">When the creation of the session fails.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    public static async Task<WebTransportSession> ConnectAsync(WebTransportSessionCreationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        HttpRequestMessage requestMessage = new(HttpMethod.Connect, options.Uri)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        requestMessage.Options.Set(
            Http3ExtendedConnectManager.RequestOptionsKey,
            static (Http3ExtendedConnectManagerCreationOptions options) => new MsQuicWebTransportExtendedConnectManager(options)
            );
        requestMessage.Headers.Protocol = "webtransport";
        if (options.AvailableSubProtocols != null)
        {
            requestMessage.Headers.Add("WT-Available-Protocols", options.AvailableSubProtocols);
        }

        HttpMessageInvoker httpMessageInvoker = options.HttpMessageInvoker ?? s_sharedHttpMessageInvoker.Value;

        HttpResponseMessage response;
        try
        {
            Task<HttpResponseMessage> sendTask = httpMessageInvoker is HttpClient client
                                ? client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                : httpMessageInvoker.SendAsync(requestMessage, cancellationToken);
            response = await sendTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // TODO: handle case where user provides message invoker that does not support http/3 with special WT Error and message and write test for it
            throw new WebTransportException(WebTransportError.SessionRefused, "Failed to create a WebTransport session.", e);
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
            wtExtendedConnectManager.FinishedUsingConnectStream(extendedConnectContent.ConnectStream);
            throw;
        }

        WebTransportSession session = wtExtendedConnectManager.CreateSession(
            extendedConnectContent.ConnectStream,
            extendedConnectContent.ConnectStreamBuffer,
            extendedConnectContent.QuicConnection,
            options.GracefulShutdownHandler,
            selectedSubprotocol);

        await SetInitialOptionsForPeerAsync(session, options, cancellationToken).ConfigureAwait(false);

        return session;
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
                    throw new WebTransportException(WebTransportError.HeaderError, "Multiple WT-Protocol headers received from the server.");
                }

                try
                {
                    StructuredFieldValuesForHttp.ValidateToken(value);
                }
                catch (Exception e)
                {
                    throw new WebTransportException(WebTransportError.HeaderError, $"The server selected a protocol '{value}' that was not offered by the client.", e);
                }

                if (!availableSubProtocols.Contains(value))
                {
                    throw new WebTransportException(WebTransportError.HeaderError, $"The server selected a protocol '{value}' that was not offered by the client.");
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
