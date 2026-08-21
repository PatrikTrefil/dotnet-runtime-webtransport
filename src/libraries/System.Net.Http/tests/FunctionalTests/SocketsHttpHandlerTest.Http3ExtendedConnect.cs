// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Net.Quic;
using System.Net.Test.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
#if !TARGET_BROWSER && !TARGET_WASI && !TARGETS_BROWSER
    [ConditionalClass(typeof(HttpClientHandlerTestBase), nameof(IsHttp3Supported))]
    public sealed class SocketsHttpHandler_Http3ExtendedConnect_Test : HttpClientHandlerTestBase
    {
        protected override Version UseVersion => HttpVersion.Version30;

        public SocketsHttpHandler_Http3ExtendedConnect_Test(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task Connect_Http3_MissingExtendedConnectManager_ThrowsExtendedConnectNotSupported()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 }
                    );

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                using HttpRequestMessage request = CreateRequest(HttpMethod.Connect, server.Address, UseVersion, exactVersion: true);
                request.Headers.Protocol = "foo";

                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
                Assert.Equal(HttpRequestError.ExtendedConnectNotSupported, ex.HttpRequestError);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_ServerWithoutEnableConnectSetting_ThrowsWithExceptionData()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync();

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        validateAndProcessServerSettings: _ => Interlocked.Increment(ref validateAndProcessServerSettingsCallCount),
                        reserveSession: () => Interlocked.Increment(ref reserveSessionCallCount)));

                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
                Assert.Equal(HttpRequestError.ExtendedConnectNotSupported, ex.HttpRequestError);
                Assert.False((bool)ex.Data["SETTINGS_ENABLE_CONNECT_PROTOCOL"]!);
                Assert.Equal(0, validateAndProcessServerSettingsCallCount);
                Assert.Equal(0, reserveSessionCallCount);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_ServerConnectionClosedBeforeSettings_FailsWithoutHanging()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.AcceptConnectionWithoutSettingsAsync();
                await connection.CloseAsync(Http3LoopbackConnection.H3_INTERNAL_ERROR);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        validateAndProcessServerSettings: _ => Interlocked.Increment(ref validateAndProcessServerSettingsCallCount),
                        reserveSession: () => Interlocked.Increment(ref reserveSessionCallCount)));

                HttpProtocolException ex = await Assert.ThrowsAsync<HttpProtocolException>(() => client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(Http3LoopbackConnection.H3_INTERNAL_ERROR, ex.ErrorCode);
                Assert.Equal(0, validateAndProcessServerSettingsCallCount);
                Assert.Equal(0, reserveSessionCallCount);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_ManagerValidateServerSettingsThrows_WrappedAsExtendedConnectNotSupported()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 },
                    new Http3SettingsEntry { SettingId = Http3SettingType.WebTransportMaxSessions, Value = 1 });

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                InvalidOperationException expected = new("validate failure");
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        validateAndProcessServerSettings: _ =>
                        {
                            Interlocked.Increment(ref validateAndProcessServerSettingsCallCount);
                            throw expected;
                        },
                        reserveSession: () => Interlocked.Increment(ref reserveSessionCallCount)));

                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
                Assert.Equal(HttpRequestError.ExtendedConnectNotSupported, ex.HttpRequestError);
                Assert.Same(expected, ex.InnerException);
                Assert.Equal(1, validateAndProcessServerSettingsCallCount);
                Assert.Equal(0, reserveSessionCallCount);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_ManagerReserveSessionThrows_WrappedAsExtendedConnectNotSupported()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                InvalidOperationException expected = new("reserve failure");
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        validateAndProcessServerSettings: _ => Interlocked.Increment(ref validateAndProcessServerSettingsCallCount),
                        reserveSession: () =>
                        {
                            Interlocked.Increment(ref reserveSessionCallCount);
                            throw expected;
                        }));

                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
                Assert.Equal(HttpRequestError.ExtendedConnectNotSupported, ex.HttpRequestError);
                Assert.Same(expected, ex.InnerException);
                Assert.Equal(1, validateAndProcessServerSettingsCallCount);
                Assert.Equal(1, reserveSessionCallCount);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_Non200Response_ReleasesSession_AllowsSecondConnect()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream first = await connection.AcceptRequestStreamAsync();
                await first.ReadRequestDataAsync(readBody: false);
                await first.SendResponseAsync(HttpStatusCode.BadRequest, content: null);

                await using Http3LoopbackStream second = await connection.AcceptRequestStreamAsync();
                await second.ReadRequestDataAsync(readBody: false);
                await second.SendResponseHeadersAsync(HttpStatusCode.OK);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                int releaseSessionAfterFailedHandshakeCallCount = 0;
                Http3ExtendedConnectManager.Http3ExtendedConnectManagerValueFactory managerFactory = CreateExtendedConnectManagerFactory(
                    managerId,
                    validateAndProcessServerSettings: _ => Interlocked.Increment(ref validateAndProcessServerSettingsCallCount),
                    reserveSession: () => Interlocked.Increment(ref reserveSessionCallCount),
                    releaseSessionAfterFailedHandshake: _ =>
                    {
                        Interlocked.Increment(ref releaseSessionAfterFailedHandshakeCallCount);
                        return Task.CompletedTask;
                    });

                using (HttpRequestMessage first = CreateExtendedConnectRequest(server.Address, "foo", managerFactory))
                using (HttpResponseMessage firstResponse = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, firstResponse.StatusCode);
                }

                using (HttpRequestMessage second = CreateExtendedConnectRequest(server.Address, "foo", managerFactory))
                using (HttpResponseMessage secondResponse = await client.SendAsync(second, HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
                }

                Assert.Equal(2, validateAndProcessServerSettingsCallCount);
                Assert.Equal(2, reserveSessionCallCount);
                Assert.Equal(1, releaseSessionAfterFailedHandshakeCallCount);
                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_SendsProtocolPseudoHeader()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);
            const string protocol = "foo-proto";

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream requestStream = await connection.AcceptRequestStreamAsync();
                HttpRequestData request = await requestStream.ReadRequestDataAsync(readBody: false);
                Assert.Equal(protocol, request.GetSingleHeaderValue(":protocol"));

                await requestStream.SendResponseHeadersAsync(HttpStatusCode.OK);
                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int managerId = ExtendedConnectManagerController.NextId();
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    protocol,
                    CreateExtendedConnectManagerFactory(managerId));
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_200Response_DoesNotImmediatelyCloseRequestStream()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            TaskCompletionSource clientValidatedConnectStreamOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream requestStream = await connection.AcceptRequestStreamAsync();
                await requestStream.ReadRequestDataAsync(readBody: false);
                await requestStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                await clientValidatedConnectStreamOpen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int managerId = ExtendedConnectManagerController.NextId();
                int validateAndProcessServerSettingsCallCount = 0;
                int reserveSessionCallCount = 0;
                int releaseSessionAfterFailedHandshakeCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        validateAndProcessServerSettings: _ => Interlocked.Increment(ref validateAndProcessServerSettingsCallCount),
                        reserveSession: () => Interlocked.Increment(ref reserveSessionCallCount),
                        releaseSessionAfterFailedHandshake: _ =>
                        {
                            Interlocked.Increment(ref releaseSessionAfterFailedHandshakeCallCount);
                            return Task.CompletedTask;
                        }));
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(1, validateAndProcessServerSettingsCallCount);
                Assert.Equal(1, reserveSessionCallCount);
                Assert.Equal(0, releaseSessionAfterFailedHandshakeCallCount);

                Http3ExtendedConnectContent extendedConnectContent = Assert.IsType<Http3ExtendedConnectContent>(response.Content);
                Assert.False(extendedConnectContent.ConnectStream.ReadsClosed.IsCompleted);
                Assert.False(extendedConnectContent.ConnectStream.WritesClosed.IsCompleted);

                clientValidatedConnectStreamOpen.TrySetResult();
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task Connect_Http3_RemoveSessionAsync_OnlyOpenSession_ClosesConnection()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            TaskCompletionSource serverObservedConnectionClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream connectStream = await connection.AcceptRequestStreamAsync();
                await connectStream.ReadRequestDataAsync(readBody: false);
                await connectStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                await connection.WaitForClientDisconnectAsync(refuseNewRequests: false);
                serverObservedConnectionClose.TrySetResult();
            });

            Task clientTask = Task.Run(async () =>
            {
                using SocketsHttpHandler handler = CreateSocketsHttpHandler(allowAllCertificates: true);
                handler.PooledConnectionIdleTimeout = TimeSpan.Zero;
                // Keep this at one so the extended CONNECT session stays on a single pooled connection,
                // making idle-timeout-driven connection shutdown deterministic in this test.
                handler.MaxConnectionsPerServer = 1;
                using HttpClient client = CreateHttpClient(handler);
                int managerId = ExtendedConnectManagerController.NextId();
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(managerId));
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                Http3ExtendedConnectContent extendedConnectContent = Assert.IsType<Http3ExtendedConnectContent>(response.Content);
                Http.Tests.TestHttp3ExtendedConnectManager manager = Assert.IsType<Http.Tests.TestHttp3ExtendedConnectManager>(extendedConnectContent.ExtendedConnectManager);

                await manager.RemoveSessionForTestAsync(extendedConnectContent.ConnectStream);
                await serverObservedConnectionClose.Task.WaitAsync(TimeSpan.FromSeconds(10));
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Theory]
        [InlineData(QuicStreamType.Bidirectional)]
        [InlineData(QuicStreamType.Unidirectional)]
        public async Task Connect_Http3_ReturningOutboundStream_AffectsRequestQuotaOnlyForBidirectionalStreams(QuicStreamType streamType)
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer(new Http3Options
            {
                // The CONNECT request consumes one bidirectional slot. The bidirectional variant needs one
                // extra slot for the manager-opened stream so we can verify that returning it re-enables requests.
                MaxInboundBidirectionalStreams = streamType == QuicStreamType.Bidirectional ? 2 : 1,
                MaxInboundUnidirectionalStreams = 3
            });

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection extendedConnectConnection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream connectStream = await extendedConnectConnection.AcceptRequestStreamAsync();
                await connectStream.ReadRequestDataAsync(readBody: false);
                await connectStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                QuicStream outboundStream = await extendedConnectConnection.AcceptQuicStreamAsync();
                Assert.Equal(streamType == QuicStreamType.Bidirectional, outboundStream.CanWrite);
                await outboundStream.DisposeAsync();

                // Returning a bidirectional extended-connect stream should restore the shared bidirectional
                // request quota on this connection. Returning a unidirectional one must not.
                if (streamType == QuicStreamType.Bidirectional)
                {
                    await using Http3LoopbackStream requestStream = await extendedConnectConnection.AcceptRequestStreamAsync();
                    HttpRequestData request = await requestStream.ReadRequestDataAsync(readBody: false);
                    Assert.Equal(HttpMethod.Get.Method, request.Method);
                    await requestStream.SendResponseAsync(HttpStatusCode.OK);
                }
                else
                {
                    await using Http3LoopbackConnection regularRequestConnection = await server.EstablishConnectionAsync();
                    await using Http3LoopbackStream requestStream = await regularRequestConnection.AcceptRequestStreamAsync();
                    HttpRequestData request = await requestStream.ReadRequestDataAsync(readBody: false);
                    Assert.Equal(HttpMethod.Get.Method, request.Method);
                    await requestStream.SendResponseAsync(HttpStatusCode.OK);
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using SocketsHttpHandler handler = CreateSocketsHttpHandler(allowAllCertificates: true);
                handler.EnableMultipleHttp3Connections = true;
                using HttpClient client = CreateHttpClient(handler);

                int managerId = ExtendedConnectManagerController.NextId();
                using HttpRequestMessage connectRequest = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(managerId));
                using HttpResponseMessage connectResponse = await client.SendAsync(connectRequest, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

                Http3ExtendedConnectContent extendedConnectContent = Assert.IsType<Http3ExtendedConnectContent>(connectResponse.Content);
                Http.Tests.TestHttp3ExtendedConnectManager manager = Assert.IsType<Http.Tests.TestHttp3ExtendedConnectManager>(extendedConnectContent.ExtendedConnectManager);

                // This must use the quota bucket matching the stream type.
                QuicStream outboundStream =
                    await manager.OpenOutboundStreamForTestAsync(streamType, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

                await outboundStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(streamType);

                using HttpRequestMessage regularRequest = CreateRequest(HttpMethod.Get, server.Address, UseVersion, exactVersion: true);
                using HttpResponseMessage regularResponse = await client.SendAsync(regularRequest).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, regularResponse.StatusCode);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Theory]
        [InlineData(QuicStreamType.Bidirectional)]
        [InlineData(QuicStreamType.Unidirectional)]
        public async Task Connect_Http3_OutboundStream_WhenQuotaForStreamTypeIsFilled_WaitsUntilMatchingStreamIsReturned(QuicStreamType streamType)
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer(new Http3Options
            {
                // CONNECT itself uses one bidirectional stream, so the bidirectional variant needs one extra slot
                // for the first extended-connect stream before the second one can block on quota.
                MaxInboundBidirectionalStreams = streamType == QuicStreamType.Bidirectional ? 2 : 1,
                // The client HTTP/3 control stream consumes one unidirectional slot before any extended-connect streams open.
                MaxInboundUnidirectionalStreams = streamType == QuicStreamType.Unidirectional ? 2 : 1
            });

            TaskCompletionSource firstStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource disposeFirstAcceptedStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream connectStream = await connection.AcceptRequestStreamAsync();
                await connectStream.ReadRequestDataAsync(readBody: false);
                await connectStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                await using (QuicStream  acceptedFirstStream = await connection.AcceptQuicStreamAsync())
                {
                    firstStreamAccepted.TrySetResult();
                    await disposeFirstAcceptedStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                await using QuicStream acceptedSecondStream = await connection.AcceptQuicStreamAsync();
                secondStreamAccepted.TrySetResult();
            });

            Task clientTask = Task.Run(async () =>
            {
                using SocketsHttpHandler handler = CreateSocketsHttpHandler(allowAllCertificates: true);
                handler.EnableMultipleHttp3Connections = true;
                using HttpClient client = CreateHttpClient(handler);

                int managerId = ExtendedConnectManagerController.NextId();
                using HttpRequestMessage connectRequest = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(managerId));
                using HttpResponseMessage connectResponse = await client.SendAsync(connectRequest, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

                Http3ExtendedConnectContent extendedConnectContent = Assert.IsType<Http3ExtendedConnectContent>(connectResponse.Content);
                Http.Tests.TestHttp3ExtendedConnectManager manager = Assert.IsType<Http.Tests.TestHttp3ExtendedConnectManager>(extendedConnectContent.ExtendedConnectManager);

                QuicStream firstStream =
                    await manager.OpenOutboundStreamForTestAsync(streamType, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                firstStream.WriteByte(1); // actually open the stream

                await firstStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Task<QuicStream> secondOpenTask = manager.OpenOutboundStreamForTestAsync(streamType, CancellationToken.None);
                Assert.False(secondOpenTask.IsCompleted);

                await firstStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(streamType);
                disposeFirstAcceptedStream.TrySetResult();

                QuicStream secondStream = await secondOpenTask.WaitAsync(TimeSpan.FromSeconds(10));
                secondStream.WriteByte(1); // actually open the stream
                await secondStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await secondStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(streamType);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Theory]
        [InlineData(QuicStreamType.Bidirectional)]
        [InlineData(QuicStreamType.Unidirectional)]
        public async Task Connect_Http3_ReturningOutboundStream_RestoresOnlyMatchingQuota(QuicStreamType primaryStreamType)
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer(new Http3Options
            {
                // CONNECT consumes one bidirectional stream; one additional bidirectional slot is needed to exhaust
                // bidirectional extended-connect streams in either parameterization.
                MaxInboundBidirectionalStreams = 2,
                // The client HTTP/3 control stream uses one unidirectional slot, leaving one for the test's uni stream.
                MaxInboundUnidirectionalStreams = 2
            });

            QuicStreamType secondaryStreamType = primaryStreamType == QuicStreamType.Bidirectional ?
                QuicStreamType.Unidirectional :
                QuicStreamType.Bidirectional;

            TaskCompletionSource primaryStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondaryStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource disposePrimaryAcceptedStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource disposeSecondaryAcceptedStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondPrimaryStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondSecondaryStreamAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream connectStream = await connection.AcceptRequestStreamAsync();
                await connectStream.ReadRequestDataAsync(readBody: false);
                await connectStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                // Accept one outbound stream of each type so the client can independently exhaust both quota buckets.
                QuicStream acceptedPrimaryStream = await connection.AcceptQuicStreamAsync();
                primaryStreamAccepted.TrySetResult();

                QuicStream acceptedSecondaryStream = await connection.AcceptQuicStreamAsync();
                secondaryStreamAccepted.TrySetResult();

                // Returning only the primary stream type should unblock only the matching open.
                await disposePrimaryAcceptedStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await acceptedPrimaryStream.DisposeAsync();

                await using QuicStream acceptedSecondPrimaryStream = await connection.AcceptQuicStreamAsync();
                secondPrimaryStreamAccepted.TrySetResult();

                // The secondary type must stay blocked until its own stream is returned.
                await disposeSecondaryAcceptedStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await acceptedSecondaryStream.DisposeAsync();

                await using QuicStream acceptedSecondSecondaryStream = await connection.AcceptQuicStreamAsync();
                secondSecondaryStreamAccepted.TrySetResult();
            });

            Task clientTask = Task.Run(async () =>
            {
                using SocketsHttpHandler handler = CreateSocketsHttpHandler(allowAllCertificates: true);
                handler.EnableMultipleHttp3Connections = true;
                using HttpClient client = CreateHttpClient(handler);

                int managerId = ExtendedConnectManagerController.NextId();
                using HttpRequestMessage connectRequest = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(managerId));
                using HttpResponseMessage connectResponse = await client.SendAsync(connectRequest, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

                Http3ExtendedConnectContent extendedConnectContent = Assert.IsType<Http3ExtendedConnectContent>(connectResponse.Content);
                Http.Tests.TestHttp3ExtendedConnectManager manager = Assert.IsType<Http.Tests.TestHttp3ExtendedConnectManager>(extendedConnectContent.ExtendedConnectManager);

                QuicStream firstPrimaryStream =
                    await manager.OpenOutboundStreamForTestAsync(primaryStreamType, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                // Actually open the QUIC stream on the wire so the server can observe and account for it.
                firstPrimaryStream.CompleteWrites();
                await primaryStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                QuicStream firstSecondaryStream =
                    await manager.OpenOutboundStreamForTestAsync(secondaryStreamType, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                firstSecondaryStream.CompleteWrites(); // Actually open the QUIC stream
                await secondaryStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // Both stream-type quotas are full now, so additional opens of either type must wait.
                Task<QuicStream> secondPrimaryOpenTask = manager.OpenOutboundStreamForTestAsync(primaryStreamType, CancellationToken.None);
                Task<QuicStream> secondSecondaryOpenTask = manager.OpenOutboundStreamForTestAsync(secondaryStreamType, CancellationToken.None);
                Assert.False(secondPrimaryOpenTask.IsCompleted);
                Assert.False(secondSecondaryOpenTask.IsCompleted);

                // Returning the primary stream must not replenish the secondary stream type.
                await firstPrimaryStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(primaryStreamType);
                disposePrimaryAcceptedStream.TrySetResult();

                // The second primary stream can now complete, but the secondary one must still be blocked.
                QuicStream secondPrimaryStream = await secondPrimaryOpenTask.WaitAsync(TimeSpan.FromSeconds(10));
                secondPrimaryStream.CompleteWrites(); // Actually open the QUIC stream
                await secondPrimaryStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.False(secondSecondaryOpenTask.IsCompleted);

                // Once the secondary stream is returned, the blocked secondary open can complete too.
                await firstSecondaryStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(secondaryStreamType);
                disposeSecondaryAcceptedStream.TrySetResult();

                QuicStream secondSecondaryStream = await secondSecondaryOpenTask.WaitAsync(TimeSpan.FromSeconds(10));
                secondSecondaryStream.CompleteWrites(); // Actually open the QUIC stream
                await secondSecondaryStreamAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                await secondPrimaryStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(primaryStreamType);
                await secondSecondaryStream.DisposeAsync();
                manager.RemoveOutboundStreamForTest(secondaryStreamType);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Connect_Http3_ServerInitiatedStream_InvokesManagerProcessReceivedStream(bool useUnidirectionalStream)
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            using Barrier barrier = new(2);
            TaskCompletionSource<(QuicStreamType StreamType, byte[] Data)> receivedStreamObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int managerId = ExtendedConnectManagerController.NextId();
            long testUnidirectionalStreamType = Http.Tests.TestHttp3ExtendedConnectManager.GetTestUnidirectionalStreamType(managerId);
            long testBidirectionalStreamSignalValue = Http.Tests.TestHttp3ExtendedConnectManager.GetTestBidirectionalStreamSignalValue(managerId);
            byte[] expectedData = new byte[] { 0x01, 0x02, 0x03 };

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = await server.EstablishConnectionAsync(
                    new Http3SettingsEntry { SettingId = Http3SettingType.EnableConnect, Value = 1 });

                await using Http3LoopbackStream connectStream = await connection.AcceptRequestStreamAsync();
                await connectStream.ReadRequestDataAsync(readBody: false);
                await connectStream.SendResponseHeadersAsync(HttpStatusCode.OK);

                await using Http3LoopbackStream stream = useUnidirectionalStream ?
                    await connection.OpenUnidirectionalStreamAsync() :
                    await connection.OpenBidirectionalStreamAsync();

                if (useUnidirectionalStream)
                {
                    byte[] streamTypeBytes = new byte[8];
                    int streamTypeLength = VariableLengthIntegerHelper.EncodeVariableLengthInteger(testUnidirectionalStreamType, streamTypeBytes);
                    await stream.Stream.WriteAsync(streamTypeBytes.AsMemory(0, streamTypeLength));
                }
                else
                {
                    byte[] signalBytes = new byte[8];
                    int signalLength = VariableLengthIntegerHelper.EncodeVariableLengthInteger(testBidirectionalStreamSignalValue, signalBytes);
                    await stream.Stream.WriteAsync(signalBytes.AsMemory(0, signalLength));
                }

                await stream.Stream.WriteAsync(expectedData);
                stream.Stream.CompleteWrites();

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                int processReceivedStreamCallCount = 0;
                using HttpRequestMessage request = CreateExtendedConnectRequest(
                    server.Address,
                    "foo",
                    CreateExtendedConnectManagerFactory(
                        managerId,
                        processReceivedStreamAsync: async (streamType, initialData, stream) =>
                        {
                            try
                            {
                                if (Interlocked.Increment(ref processReceivedStreamCallCount) != 1)
                                {
                                    Exception ex = new InvalidOperationException("ProcessReceivedStreamAsync was called more than once.");
                                    receivedStreamObserved.TrySetException(ex);
                                    throw ex;
                                }

                                using MemoryStream ms = new();
                                await stream.CopyToAsync(ms);
                                byte[] trailingData = ms.ToArray();
                                byte[] allData = new byte[initialData.Length + trailingData.Length];
                                Buffer.BlockCopy(initialData, 0, allData, 0, initialData.Length);
                                Buffer.BlockCopy(trailingData, 0, allData, initialData.Length, trailingData.Length);
                                receivedStreamObserved.TrySetResult((streamType, allData));
                            }
                            catch (Exception ex)
                            {
                                receivedStreamObserved.TrySetException(ex);
                                throw;
                            }
                            finally
                            {
                                await stream.DisposeAsync();
                            }
                        }));
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                (QuicStreamType receivedStreamType, byte[] receivedData) = await receivedStreamObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(useUnidirectionalStream ? QuicStreamType.Unidirectional : QuicStreamType.Bidirectional, receivedStreamType);
                Assert.Equal(expectedData, receivedData);
                Assert.Equal(1, processReceivedStreamCallCount);

                barrier.SignalAndWait(TestHelper.PassingTestTimeout);
            });

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(20_000);
        }

        private HttpRequestMessage CreateExtendedConnectRequest(
            Uri uri,
            string protocol,
            Http3ExtendedConnectManager.Http3ExtendedConnectManagerValueFactory managerFactory)
        {
            HttpRequestMessage request = CreateRequest(HttpMethod.Connect, uri, UseVersion, exactVersion: true);
            request.Headers.Protocol = protocol;
            request.Options.Set(Http3ExtendedConnectManager.RequestOptionsKey, managerFactory);
            return request;
        }

        private static Http3ExtendedConnectManager.Http3ExtendedConnectManagerValueFactory CreateExtendedConnectManagerFactory(
            int managerId,
            Func<Task>? processGoAwayAsync = null,
            Func<QuicStreamType, byte[], QuicStream, Task>? processReceivedStreamAsync = null,
            Action<Dictionary<long, long>>? validateAndProcessServerSettings = null,
            Action? reserveSession = null,
            Func<QuicStream?, Task>? releaseSessionAfterFailedHandshake = null)
            => options => new Http.Tests.TestHttp3ExtendedConnectManager(
                options: options,
                processGoAwayAsync: processGoAwayAsync,
                processReceivedStreamAsync: processReceivedStreamAsync,
                unidirectionalStreamType: Http.Tests.TestHttp3ExtendedConnectManager.GetTestUnidirectionalStreamType(managerId),
                bidirectionalStreamSignalValue: Http.Tests.TestHttp3ExtendedConnectManager.GetTestBidirectionalStreamSignalValue(managerId),
                validateAndProcessServerSettings: validateAndProcessServerSettings,
                reserveSession: reserveSession,
                releaseSessionAfterFailedHandshake: releaseSessionAfterFailedHandshake);
    }

    public sealed class ExtendedConnectManagerController
    {
        private static int s_nextId;
        public static int NextId() => Interlocked.Increment(ref s_nextId);
    }
#endif
}
