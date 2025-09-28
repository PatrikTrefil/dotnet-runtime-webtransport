// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Threading.Tasks;

namespace System.Net.WebTransport.Functional.Tests;

internal static class WebTransportSessionTestHelper
{
    public static async Task AssertAllOperationsOnSessionThrowAsync<TException>(WebTransportSession session, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Unidirectional)),
            await Assert.ThrowsAsync<TException>(() => session.OpenOutboundStreamAsync(WebTransportStreamType.Bidirectional)),
            await Assert.ThrowsAsync<TException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Unidirectional)),
            await Assert.ThrowsAsync<TException>(() => session.AcceptInboundStreamAsync(WebTransportStreamType.Bidirectional)),
            await Assert.ThrowsAsync<TException>(() => session.SetUnidirectionalStreamCountLimitForPeerAsync(1)),
            await Assert.ThrowsAsync<TException>(() => session.SetBidirectionalStreamCountLimitForPeerAsync(1)),
            await Assert.ThrowsAsync<TException>(() => session.SetDataSentLimitForPeerAsync(1)),
            await Assert.ThrowsAsync<TException>(() => session.RequestCloseAsync()),
            await Assert.ThrowsAsync<TException>(() => session.CloseAsync(1, "")),
            Assert.Throws<TException>(session.CloseAsync),
        ];

        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }
}
