// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Runtime.CompilerServices;

// TODO: the links to WT over HTTP/3 sections should be present only on the derived class. The rest should link to the WT overview doc

namespace System.Net.WebTransport;

public sealed class WebTransportSessionCreationOptions
{
    /// <summary>
    /// This function is invoked when peer requests a graceful shutdown. The session may be used to send more data,
    /// but is should be terminated as soon as possible.
    /// </summary>
    /// <remarks>
    /// The default handler calls <see cref="WebTransportSession.CloseAsync(CancellationToken)"/>.
    /// This handler is called when an HTTP GOAWAY frame is received or the DRAIN_WEBTRANSPORT_SESSION capsule is received.
    /// The function should never throw. If it throws, the session is closed immediately.
    /// </remarks>
    /// <seealso href="https://datatracker.ietf.org/doc/html/rfc9114#name-goaway"/>
    public Func<WebTransportSession, Task> GracefulShutdownHandler { get; init; } = (session) => { session.CloseAsync(); return Task.CompletedTask; };
    /// <summary>
    /// List of protocols that may be used in the session in order of preference.
    /// The selected protocol will be available in <see cref="WebTransportSession.SubProtocol"/>.
    /// </summary>
    /// <remarks>
    /// Note that the server may choose not to use any of the provided protocols. In that case <see cref="WebTransportSession.SubProtocol"/> will be null.
    /// The value must be serializable as a list of tokens according to RFC 8941.
    /// </remarks>
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
                throw new ArgumentException($"The token at index {i} is not valid.", paramName, e);
            }
        }
    }

    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
    public long InitialUnidirectionalStreamCountLimitForPeer
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }

    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not in the range [0, 2^62).</exception>
    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
    public long InitialBidirectionalStreamCountLimitForPeer
    {
        get;
        init
        {
            VariableLengthIntegerValidator.ThrowIfInvalid(value);
            field = value;
        }
    }

    /// <summary>
    /// Default value is zero.
    /// The value must be in the range [0, 2^62).
    /// </summary>
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
}
