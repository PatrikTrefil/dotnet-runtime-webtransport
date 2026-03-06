// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Net.Quic;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChannelItem = (System.Net.ArrayBuffer ArrayBuffer, System.Net.Quic.QuicStream QuicStream);

namespace System.Net.WebTransport;

internal static class WebTransportPendingStreamCleanup
{
    internal static void CloseAndDisposeAllStreamsInChannel(Channel<ChannelItem>? channel, Http3ErrorCode httpErrorCode)
    {
        if (channel is null)
        {
            return;
        }

        while (channel.Reader.TryRead(out ChannelItem item))
        {
            item.ArrayBuffer.Dispose();
            item.QuicStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
            item.QuicStream.Dispose();
        }
    }

    internal static async ValueTask CloseAndDisposeAllStreamsInChannelAsync(Channel<ChannelItem>? channel, Http3ErrorCode httpErrorCode)
    {
        if (channel is null)
        {
            return;
        }

        while (channel.Reader.TryRead(out ChannelItem item))
        {
            item.ArrayBuffer.Dispose();
            item.QuicStream.Abort(QuicAbortDirection.Both, (long)httpErrorCode);
            await item.QuicStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
