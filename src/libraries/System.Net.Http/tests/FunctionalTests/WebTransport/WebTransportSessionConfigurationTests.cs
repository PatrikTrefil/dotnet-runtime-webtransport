// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionConfigurationTests : WebTransportTestBase
{
    public WebTransportSessionConfigurationTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;
    private const long MaxUnidirectionalStreamLimitCapsuleCode = 0x190B4D40;
    private const long MaxBidirectionalStreamLimitCapsuleCode = 0x190B4D3F;
    private const long MaxDataCapsuleCode = 0x190B4D3D;

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetUnidirectionalStreamCountLimitSendsCorrectCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedUnidirectionalStreamCountLimit = 1;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedMaxUnidirectionalStreams, bytesReadMaxUnidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(MaxUnidirectionalStreamLimitCapsuleCode, capsuleCode);
            Assert.Equal(expectedUnidirectionalStreamCountLimit, receivedMaxUnidirectionalStreams);
            Assert.Equal(capsuleValueLength, bytesReadMaxUnidirectionalStreams);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);
            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetBidirectionalStreamCountLimitSendsCorrectCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedBidirectionalStreamCountLimit = 2;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedMaxBidirectionalStreams, bytesReadMaxBidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(MaxBidirectionalStreamLimitCapsuleCode, capsuleCode);
            Assert.Equal(expectedBidirectionalStreamCountLimit, receivedMaxBidirectionalStreams);
            Assert.Equal(capsuleValueLength, bytesReadMaxBidirectionalStreams);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);
            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetMaxDataSentLimitSendsCorrectCapsule()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedMaxDataSentLimit = 1024;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedMaxDataSentLimit, bytesReadMaxDataSentLimit) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(MaxDataCapsuleCode, capsuleCode);
            Assert.Equal(expectedMaxDataSentLimit, receivedMaxDataSentLimit);
            Assert.Equal(capsuleValueLength, bytesReadMaxDataSentLimit);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetMaxDataSentLimitForPeerAsync(expectedMaxDataSentLimit);
            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetUnidirectionalStreamCountLimitUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            int expectedUnidirectionalStreamCountLimit = 1024;
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);

            Assert.Equal(expectedUnidirectionalStreamCountLimit, session.UnidirectionalStreamCountLimitForPeer);
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await Task.WhenAll(clientTask);
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetBidirectionalStreamCountLimitUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            int expectedBidirectionalStreamCountLimit = 1024;
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);

            Assert.Equal(expectedBidirectionalStreamCountLimit, session.BidirectionalStreamCountLimitForPeer);
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await Task.WhenAll(clientTask);
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SetMaxDataSentLimitUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            int expectedMaxDataSentLimit = 1024;
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.SetMaxDataSentLimitForPeerAsync(expectedMaxDataSentLimit);

            Assert.Equal(expectedMaxDataSentLimit, session.MaxDataSentLimitForPeer);
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            await Task.WhenAll(clientTask);
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task SendAllCapsuleTypesSendsAndReceivesCorrectCapsules()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedUnidirectionalStreamCountLimit = 11;
        int expectedBidirectionalStreamCountLimit = 22;
        int expectedMaxDataSentLimit = 3333;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            // Unidirectional Stream Count Limit Capsule
            var (capsuleCode1, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (capsuleValueLength1, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (receivedUnidirectionalStreamCountLimit, bytesReadUnidirectional) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            // Bidirectional Stream Count Limit Capsule
            var (capsuleCode2, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (capsuleValueLength2, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (receivedBidirectionalStreamCountLimit, bytesReadBidirectional) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            // Max Data Sent Limit Capsule
            var (capsuleCode3, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (capsuleValueLength3, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);
            var (receivedMaxDataSentLimit, bytesReadMaxData) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(MaxUnidirectionalStreamLimitCapsuleCode, capsuleCode1);
            Assert.Equal(expectedUnidirectionalStreamCountLimit, receivedUnidirectionalStreamCountLimit);
            Assert.Equal(capsuleValueLength1, bytesReadUnidirectional);

            Assert.Equal(MaxBidirectionalStreamLimitCapsuleCode, capsuleCode2);
            Assert.Equal(expectedBidirectionalStreamCountLimit, receivedBidirectionalStreamCountLimit);
            Assert.Equal(capsuleValueLength2, bytesReadBidirectional);

            Assert.Equal(MaxDataCapsuleCode, capsuleCode3);
            Assert.Equal(expectedMaxDataSentLimit, receivedMaxDataSentLimit);
            Assert.Equal(capsuleValueLength3, bytesReadMaxData);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);
            await session.SetMaxDataSentLimitForPeerAsync(expectedMaxDataSentLimit);

            await Task.WhenAll(serverTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ReceiveAllCapsuleTypesUpdatesSessionProperties()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedUnidirectionalStreamCountLimit = 123;
        int expectedBidirectionalStreamCountLimit = 456;
        int expectedMaxDataSentLimit = 7890;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            await Task.Delay(2000);

            Assert.Equal(expectedUnidirectionalStreamCountLimit, session.UnidirectionalStreamCountLimitProvidedByPeer);
            Assert.Equal(expectedBidirectionalStreamCountLimit, session.BidirectionalStreamCountLimitProvidedByPeer);
            Assert.Equal(expectedMaxDataSentLimit, session.MaxDataSentLimitProvidedByPeer);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            // Helper to write a capsule
            void WriteCapsule(long capsuleCode, long value)
            {
                // Write capsule code
                VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, capsuleCode);

                // Encode value as variable-length integer
                Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
                int valueSizeInBytes = Test.Common.VariableLengthIntegerHelper.EncodeVariableLengthInteger(value, valueBuffer);

                // Write the length of the value encoding as a variable-length integer
                VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, valueSizeInBytes);

                // Write the encoded value bytes
                serverSession.ConnectStream.Write(valueBuffer.Slice(0, valueSizeInBytes));
            }

            WriteCapsule(MaxUnidirectionalStreamLimitCapsuleCode, expectedUnidirectionalStreamCountLimit);
            WriteCapsule(MaxBidirectionalStreamLimitCapsuleCode, expectedBidirectionalStreamCountLimit);
            WriteCapsule(MaxDataCapsuleCode, expectedMaxDataSentLimit);

            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });


        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ReceiveUnidirectionalStreamLimitCapsuleUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedLimit = 123;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Task.Delay(2000);
            Assert.Equal(expectedLimit, session.UnidirectionalStreamCountLimitProvidedByPeer);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            // Write Unidirectional Stream Limit Capsule
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxUnidirectionalStreamLimitCapsuleCode);
            Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
            int valueSizeInBytes = Test.Common.VariableLengthIntegerHelper.EncodeVariableLengthInteger(expectedLimit, valueBuffer);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, valueSizeInBytes);
            serverSession.ConnectStream.Write(valueBuffer.Slice(0, valueSizeInBytes));

            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ReceiveBidirectionalStreamLimitCapsuleUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedLimit = 456;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Task.Delay(2000);
            Assert.Equal(expectedLimit, session.BidirectionalStreamCountLimitProvidedByPeer);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            // Write Bidirectional Stream Limit Capsule
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxBidirectionalStreamLimitCapsuleCode);
            Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
            int valueSizeInBytes = Test.Common.VariableLengthIntegerHelper.EncodeVariableLengthInteger(expectedLimit, valueBuffer);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, valueSizeInBytes);
            serverSession.ConnectStream.Write(valueBuffer.Slice(0, valueSizeInBytes));

            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ReceiveMaxDataCapsuleUpdatesSessionProperty()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        int expectedLimit = 7890;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await Task.Delay(2000);
            Assert.Equal(expectedLimit, session.MaxDataSentLimitProvidedByPeer);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            // Write Max Data Capsule
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxDataCapsuleCode);
            Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
            int valueSizeInBytes = Test.Common.VariableLengthIntegerHelper.EncodeVariableLengthInteger(expectedLimit, valueBuffer);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, valueSizeInBytes);
            serverSession.ConnectStream.Write(valueBuffer.Slice(0, valueSizeInBytes));

            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
}
