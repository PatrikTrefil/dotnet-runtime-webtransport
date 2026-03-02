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
            Action<QuicStream?>? releaseSessionAfterFailedHandshake = null)
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
