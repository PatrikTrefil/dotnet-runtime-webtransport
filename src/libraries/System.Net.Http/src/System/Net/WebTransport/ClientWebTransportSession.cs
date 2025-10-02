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
    /// <exception cref="ArgumentException">When <paramref name="uri"/>  does not use https scheme.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="uri"/> is <c>null</c>.</exception>
    /// <exception cref="WebTransportException">When the creation of the session fails.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled. This exception is stored into the returned task.</exception>
    public static async Task<WebTransportSession> ConnectAsync(Uri uri, HttpMessageInvoker? httpMessageInvoker, WebTransportSessionCreationOptions? options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Scheme != "https")
        {
            throw new ArgumentException("The URI scheme must be 'https'.", nameof(uri));
        }

        httpMessageInvoker ??= s_sharedHttpMessageInvoker.Value;

        return await ConnectAsyncCore(uri, httpMessageInvoker, options ?? new WebTransportSessionCreationOptions(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WebTransportSession> ConnectAsyncCore(Uri uri, HttpMessageInvoker httpMessageInvoker, WebTransportSessionCreationOptions options, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(HttpMethod.Connect, uri)
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
            wtExtendedConnectManager.FinishedUsingConnectStream(extendedConnectContent.ConnectStream); // TODO: add test for this path
            throw;
        }

        WebTransportSession session = wtExtendedConnectManager.CreateSession(
            extendedConnectContent.ConnectStream,
            extendedConnectContent.ConnectStreamBuffer,
            extendedConnectContent.QuicConnection,
            options.GracefulShutdownHandler,
            selectedSubprotocol);

        await SetInitialOptions(session, options, cancellationToken).ConfigureAwait(false);

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

    private static async Task SetInitialOptions(WebTransportSession session, WebTransportSessionCreationOptions options, CancellationToken cancellationToken)
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
