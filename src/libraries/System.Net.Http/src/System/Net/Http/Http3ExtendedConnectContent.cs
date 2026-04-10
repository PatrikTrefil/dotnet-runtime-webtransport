// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace System.Net.Http;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
/// <summary>
/// Represents the content and transport objects produced by a successful HTTP/3 extended CONNECT request.
/// </summary>
public sealed class Http3ExtendedConnectContent : HttpContent
{
    private byte[]? _connectStreamBuffer;

    /// <summary>
    /// Gets the bytes read from the CONNECT stream while establishing the extended CONNECT session.
    /// </summary>
    public byte[] ConnectStreamBuffer
    {
        get => _connectStreamBuffer ?? throw new InvalidOperationException("Connect stream buffer has not been set");
        internal set
        {
            Debug.Assert(value != null, $"{nameof(ConnectStreamBuffer)} cannot be null");
            Debug.Assert(_connectStreamBuffer == null, $"{nameof(ConnectStreamBuffer)} can only be set once");

            _connectStreamBuffer = value;
        }
    }
    private QuicStream? _connectStream;

    /// <summary>
    /// Gets the QUIC stream used for the CONNECT request.
    /// </summary>
    public QuicStream ConnectStream
    {
        get => _connectStream ?? throw new InvalidOperationException("Connect stream has not been set");
        internal set
        {
            Debug.Assert(value != null, $"{nameof(ConnectStream)} cannot be null");
            Debug.Assert(_connectStream == null, $"{nameof(ConnectStream)} can only be set once");
            Debug.Assert(value.CanRead, $"{nameof(ConnectStream)} must be readable");
            Debug.Assert(value.CanWrite, $"{nameof(ConnectStream)} must be writable");

            _connectStream = value;
        }
    }
    private QuicConnection? _quicConnection;

    /// <summary>
    /// Gets the QUIC connection associated with the HTTP/3 connection used for the extended CONNECT request.
    /// </summary>
    public QuicConnection QuicConnection
    {
        get => _quicConnection ?? throw new InvalidOperationException("QUIC connection has not been set");
        internal set
        {
            Debug.Assert(value != null, $"{nameof(QuicConnection)} cannot be null");
            Debug.Assert(_quicConnection == null, $"{nameof(QuicConnection)} can only be set once");

            _quicConnection = value;
        }
    }
    private Http3ExtendedConnectManager? _extendedConnectManager;

    /// <summary>
    /// Gets the manager that handles protocol-specific processing for the extended CONNECT session.
    /// </summary>
    public Http3ExtendedConnectManager ExtendedConnectManager
    {
        get => _extendedConnectManager ?? throw new InvalidOperationException("Extended connect manager has not been set");
        internal set
        {
            Debug.Assert(value != null, $"{nameof(ExtendedConnectManager)} cannot be null");
            Debug.Assert(_extendedConnectManager == null, $"{nameof(ExtendedConnectManager)} can only be set once");

            _extendedConnectManager = value;
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotImplementedException();
    protected internal override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    internal override bool AllowDuplex => false;
}
