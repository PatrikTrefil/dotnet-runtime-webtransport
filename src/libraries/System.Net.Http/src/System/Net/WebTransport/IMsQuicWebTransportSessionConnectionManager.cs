// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport;

internal interface IMsQuicWebTransportSessionConnectionManager
{
    Task<QuicStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken);
    void FinishedUsingOutboundStream();
    void FinishedUsingConnectStream(QuicStream connectStream);
}
