// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !TARGET_BROWSER && !TARGET_WASI && !TARGETS_BROWSER
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http.Tests
{
    public class Http3ExtendedConnectManagerTest
    {
        private static readonly TimeSpan s_waitTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task OpenOutboundStreamAsync_ObjectDisposedException_ThrowsInvalidOperationException()
        {
            TestHttp3ExtendedConnectManager manager = new(
                options: CreateOptions(
                    openOutboundStreamAsync: (streamType, cancellationToken) => throw new ObjectDisposedException("QuicConnection"),
                    removeSessionAsync: _ => Task.CompletedTask,
                    removeOutboundStream: _ => { }));

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.OpenOutboundStreamForTestAsync(QuicStreamType.Bidirectional, CancellationToken.None));
        }

        [Fact]
        public async Task OpenOutboundStreamAsync_NonObjectDisposedException_PassesThrough()
        {
            InvalidOperationException expected = new("failure");
            TestHttp3ExtendedConnectManager manager = new(
                options: CreateOptions(
                    openOutboundStreamAsync: (streamType, cancellationToken) => throw expected,
                    removeSessionAsync: _ => Task.CompletedTask,
                    removeOutboundStream: _ => { }));

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.OpenOutboundStreamForTestAsync(QuicStreamType.Unidirectional, CancellationToken.None));

            Assert.Same(expected, ex);
        }

        [Fact]
        public async Task RemoveSessionAsync_InvokesConfiguredDelegate()
        {
            TaskCompletionSource removeSessionCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TestHttp3ExtendedConnectManager manager = new(
                options: CreateOptions(
                    openOutboundStreamAsync: (streamType, cancellationToken) => throw new InvalidOperationException("unexpected call"),
                    removeSessionAsync: _ =>
                    {
                        removeSessionCalled.TrySetResult();
                        return Task.CompletedTask;
                    },
                    removeOutboundStream: _ => { }));

            await manager.RemoveSessionForTestAsync(null!);
            await removeSessionCalled.Task.WaitAsync(s_waitTimeout);
        }

        [Theory]
        [InlineData(QuicStreamType.Bidirectional)]
        [InlineData(QuicStreamType.Unidirectional)]
        public void RemoveOutboundStream_InvokesConfiguredDelegate(QuicStreamType streamType)
        {
            QuicStreamType? releasedStreamType = null;
            TestHttp3ExtendedConnectManager manager = new(
                options: CreateOptions(
                    openOutboundStreamAsync: (streamType, cancellationToken) => throw new InvalidOperationException("unexpected call"),
                    removeSessionAsync: _ => Task.CompletedTask,
                    removeOutboundStream: releasedType => releasedStreamType = releasedType));

            manager.RemoveOutboundStreamForTest(streamType);

            Assert.Equal(streamType, releasedStreamType);
        }

        private static Http3ExtendedConnectManagerCreationOptions CreateOptions(
            Func<QuicStreamType, CancellationToken, Task<QuicStream>> openOutboundStreamAsync,
            Func<QuicStream, Task> removeSessionAsync,
            Action<QuicStreamType> removeOutboundStream)
            => new(openOutboundStreamAsync, removeSessionAsync, removeOutboundStream);
    }
}
#endif
