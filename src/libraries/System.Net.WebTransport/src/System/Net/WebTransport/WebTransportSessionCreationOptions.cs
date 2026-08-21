// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Net.Http;

namespace System.Net.WebTransport;

/// <summary>
/// Provides options used to establish a <see cref="WebTransportSession"/>.
/// </summary>
public sealed class WebTransportSessionCreationOptions
{
    /// <summary>
    /// Absolute URI of the WebTransport server to connect to.
    /// </summary>
    /// <exception cref="ArgumentException">If the URI does not use 'https' scheme or is not absolute.</exception>
    /// <exception cref="ArgumentNullException">If the provided value is <c>null</c>.</exception>
    public required Uri Uri
    {
        get;
        init
        {
            if (!value.IsAbsoluteUri)
            {
                throw new ArgumentException(SR.net_webtransport_uri_not_absolute, nameof(value));
            }

            if (value.Scheme != "https")
            {
                throw new ArgumentException(SR.net_webtransport_uri_scheme_not_https, nameof(value));
            }

            field = value;
        }
    }

    /// <summary>
    /// <see cref="HttpMessageInvoker"/> used for the initial handshake of the WebTransport session.
    /// </summary>
    public required HttpMessageInvoker HttpMessageInvoker { get; init; }

    /// <summary>
    /// This function is invoked when peer requests a graceful shutdown. The session may be used to send more data,
    /// but it should be terminated as soon as possible.
    /// </summary>
    /// <remarks>
    /// The function may be invoked multiple times.
    /// The function is invoked only when the session is in state <see cref="WebTransportSessionState.Open"/>, but the session
    /// could be closed during the execution of the function.
    ///
    /// The default handler calls <see cref="WebTransportSession.CloseAsync()"/>.
    /// This handler is called when the peer invokes <see cref="WebTransportSession.RequestCloseAsync(Threading.CancellationToken)"/>.
    /// The function should never throw.
    /// If it throws, the session transitions to <see cref="WebTransportSessionState.AbortedLocally"/> and is cleaned up immediately.
    /// </remarks>
    /// <exception cref="ArgumentNullException">When the value is set to <c>null</c>.</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#name-goaway"/>
    public Func<WebTransportSession, Task> GracefulShutdownHandler
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = async (session) => await session.CloseAsync();

    /// <summary>
    /// List of protocols that may be used in the session in order of preference.
    /// The protocol selected by the server will be available in <see cref="WebTransportSession.SubProtocol"/>.
    /// </summary>
    /// <remarks>
    /// Note that the server may choose not to use any of the provided protocols. In that case <see cref="WebTransportSession.SubProtocol"/> will be <c>null</c>.
    /// The value must be serializable as a list of tokens according to RFC 8941.
    /// </remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-overview-09#section-3.1"/>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc8941#name-serializing-a-token"/>
    /// <exception cref="ArgumentException">When the provided value is not serializable as a list of tokens according to RFC 8941.</exception>
    public string[]? AvailableSubProtocols
    {
        get;
        init
        {
            if (value != null)
            {
                ValidateAvailableSubProtocolValue(value);
                field = value;
            }
        }
    }

    private static void ValidateAvailableSubProtocolValue(string[] availableSubProtocols, [CallerArgumentExpression(nameof(availableSubProtocols))] string? paramName = null)
    {
        for (int i = 0; i < availableSubProtocols.Length; i++)
        {
            try
            {
                StructuredFieldValuesForHttp.ValidateToken(availableSubProtocols[i]);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException(SR.Format(SR.net_webtransport_invalid_subprotocol_value, i), paramName, e);
            }
        }
    }

    /// <summary>
    /// The initial value of the maximum number of unidirectional streams that the peer can create in this session.
    /// The value is communicated to the peer during the session establishment and is then stored in <see cref="WebTransportSession.UnidirectionalStreamCountLimitForPeer"/>.
    /// </summary>
    /// <value>
    /// Default value is zero.
    /// The value must be in the range [0, 65535).
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 65535).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialUnidirectionalStreamCountLimitForPeer
    {
        get;
        init
        {
            ThrowHelper.ValidateStreamCountLimit(value);
            field = value;
        }
    }

    /// <summary>
    /// The initial value of the maximum number of bidirectional streams that the peer can create in this session.
    /// The value is communicated to the peer during the session establishment and is then stored in <see cref="WebTransportSession.BidirectionalStreamCountLimitForPeer"/>.
    /// </summary>
    /// <value>
    /// Default value is zero.
    /// The value must be in the range [0, 65535).
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 65535).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
    public long InitialBidirectionalStreamCountLimitForPeer
    {
        get;
        init
        {
            ThrowHelper.ValidateStreamCountLimit(value);
            field = value;
        }
    }

    /// <summary>
    /// The initial value of the maximum amount of data (in bytes) that the peer can send in this session.
    /// The value is communicated to the peer during the session establishment and is then stored in <see cref="WebTransportSession.DataSentLimitForPeer"/>.
    /// </summary>
    /// <value>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_DATA"/>
    public long InitialDataSentLimitForPeer
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }

    /// <summary>
    /// Error code used when the stream needs to abort read or write side of the stream internally, e.g. in <see cref="WebTransportStream.DisposeAsync()"/>.
    /// </summary>
    /// <value>
    /// The value must be in the range [0, 2^32).
    /// </value>
    public required long DefaultStreamErrorCode
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0L);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, uint.MaxValue);

            field = value;
        }
    }

    /// <summary>
    /// HTTP version to use for the initial handshake of the WebTransport session.
    /// </summary>
    public required Version HttpVersion
    {
        get;
        init;
    }

    /// <summary>
    /// A policy that determines how <see cref="HttpVersion"/> is interpreted and how the final HTTP version is negotiated with the server.
    /// </summary>
    public HttpVersionPolicy HttpVersionPolicy
    {
        get;
        init;
    }
}
