// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Quic;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

internal static class WebTransportSessionTestHelper
{
    /// <summary>
    /// Copy of the internal value from MsQuicWebTransportExtendedConnectManager that is used
    /// to limit the number of pending uni/bidirectional streams per session.
    /// </summary>
    private const int s_maximumNumberOfPendingStreamsPerSession = 100;

    public static async Task AssertAllOperationsOnSessionThrowAsync<TException>(WebTransportSession session, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(async () => await session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional)),
            await Assert.ThrowsAsync<TException>(async () => await session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional)),
            await Assert.ThrowsAsync<TException>(async () => await session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional)),
            await Assert.ThrowsAsync<TException>(async () => await session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional)),
            await Assert.ThrowsAsync<TException>(async () => await session.SetUnidirectionalStreamCountLimitForPeerAsync(1)),
            await Assert.ThrowsAsync<TException>(async () => await session.SetBidirectionalStreamCountLimitForPeerAsync(1)),
            await Assert.ThrowsAsync<TException>(async () => await session.SetDataSentLimitForPeerAsync(1)),
        ];

        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    /// <summary>
    /// Opens more streams than can be pending according to the internal limit.
    /// </summary>
    /// <param name="serverSession">Session in which to open the streams.</param>
    /// <param name="streamType">Streams of which type to open.</param>
    /// <returns>All streams there were opened during the process including the rejected stream and a reference to the rejected stream. There might be more than one rejected streams in the collection.</returns>
    public static async Task<(List<QuicStream> OpenStreams, QuicStream RejectedStream)> OpenMorePendingStreamsThanAllowed(WebTransportServerSession serverSession, WebTransportStreamType streamType)
    {
        QuicStream? rejectedStream = null;
        List<QuicStream> pendingStreams = [];
        object lockObj = new();
        byte[] receiveBuffer = new byte[1];
        using SemaphoreSlim semaphore = new(0, 1);

        // One of the "open stream" operations has to fail - we don't know which one because they may be processed in any order by the client
        for (int i = 0; i < s_maximumNumberOfPendingStreamsPerSession + 1; i++)
        {
            QuicStream stream = await serverSession.OpenStreamFromServerAsync(streamType);
            pendingStreams.Add(stream);
            _ = Task.Run(async () =>
            {
                try
                {
                    await stream.WritesClosed;
                }
                catch (Exception)
                {
                    lock (lockObj)
                    {
                        if (rejectedStream == null)
                        {
                            rejectedStream = stream;
                            semaphore.Release();
                        }
                    }
                }
            });
        }

        semaphore.Wait();

        return (pendingStreams, rejectedStream);
    }
}
