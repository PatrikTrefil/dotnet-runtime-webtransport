// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Threading;
using System.IO;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionConfigurationTests : WebTransportTestBase
{
    public WebTransportSessionConfigurationTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000;
    private const long MaxUnidirectionalStreamLimitCapsuleCode = 0x190B4D40;
    private const long MaxBidirectionalStreamLimitCapsuleCode = 0x190B4D3F;
    private const long MaxDataCapsuleCode = 0x190B4D3D;
    private const long unknownCapsuleCode = 0x12345678;

    private const int minValidSizeOfMaxDataCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int maxValidSizeOfMaxDataCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private const int minValidSizeOfMaxUnidirectionalCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int maxValidSizeOfMaxUnidirectionalCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private const int minValidSizeOfMaxBidirectionalCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int maxValidSizeOfMaxBidirectionalCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private void WriteMaxDataCapsule(Stream stream, long maxDataSentLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, MaxDataCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(maxDataSentLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    private void WriteBidirectionalStreamLimitCapsule(Stream stream, long bidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, MaxBidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(bidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    private void WriteUnidirectionalStreamLimitCapsule(Stream stream, long unidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, MaxUnidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(unidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

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

            WriteUnidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedUnidirectionalStreamCountLimit);
            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedBidirectionalStreamCountLimit);
            WriteMaxDataCapsule(serverSession.ConnectStream, expectedMaxDataSentLimit);

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

            WriteUnidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit);

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

            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit);

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

            WriteMaxDataCapsule(serverSession.ConnectStream, expectedLimit);

            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(5)]
    [InlineData(10_001)] // This should trigger special handling of long unknown capsules
    public async Task ReceiveUnknownCapsuleOnConnectStream(long capsuleValueSize)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        int expectedLimit = 42;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            // Assert the valid capsule after the unknown one is received
            SpinWait.SpinUntil(() => session.BidirectionalStreamCountLimitProvidedByPeer == expectedLimit, 3000);
            Assert.Equal(expectedLimit, session.BidirectionalStreamCountLimitProvidedByPeer);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, unknownCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, capsuleValueSize);
            serverSession.ConnectStream.Write(new byte[capsuleValueSize]);
            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit); // Write a valid capsule after the unknown one
            await serverSession.ConnectStream.FlushAsync();
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(minValidSizeOfMaxDataCapsuleValue - 1)]
    [InlineData(maxValidSizeOfMaxDataCapsuleValue + 1)]
    public async Task ReceiveMaxDataCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxDataCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }


    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(minValidSizeOfMaxUnidirectionalCapsuleValue - 1)]
    [InlineData(maxValidSizeOfMaxUnidirectionalCapsuleValue + 1)]
    public async Task ReceiveMaxUnidirectionalStreamLimitCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 10000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxUnidirectionalStreamLimitCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [InlineData(minValidSizeOfMaxBidirectionalCapsuleValue - 1)]
    [InlineData(maxValidSizeOfMaxBidirectionalCapsuleValue + 1)]
    public async Task ReceiveMaxBidirectionalStreamLimitCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State == WebTransportSessionState.Closed, 3000);

            Assert.Equal(WebTransportSessionState.Closed, session.State);
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, MaxBidirectionalStreamLimitCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            serverSession.ConnectStream.Write(new byte[invalidLength]);
            await serverSession.ConnectStream.FlushAsync();

            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
}
