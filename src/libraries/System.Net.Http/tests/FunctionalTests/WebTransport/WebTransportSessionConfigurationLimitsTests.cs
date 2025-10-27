// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Threading;
using System.IO;
using System.Net.Quic;

namespace System.Net.WebTransport.Functional.Tests;

// TODO: write tests for limits enforcement (e.g. try to open more streams than allowed and see that it fails)
// TODO: write test that sends settings using HTTP/3 SETTINGS frame and asserts that the values are applied to the session
// TODO: write test that doesn't sends settings using HTTP/3 SETTINGS frame and asserts that the default values are applied to the session

/// <summary>
/// Contains tests for limits configuration of WebTransport sessions such as setting of maximum count of open unidirectional streams.
/// </summary>
[ConditionalClass(typeof(WebTransportTestBase), nameof(IsWebTransportSupported))]
public sealed class WebTransportSessionConfigurationLimitsTests : WebTransportTestBase
{
    private const long s_maxUnidirectionalStreamLimitCapsuleCode = 0x190B4D40;
    private const long s_maxBidirectionalStreamLimitCapsuleCode = 0x190B4D3F;
    private const long s_maxDataCapsuleCode = 0x190B4D3D;
    private const long s_unknownCapsuleCode = 0x12345678;

    private const int s_minValidSizeOfMaxDataCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int s_maxValidSizeOfMaxDataCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private const int s_minValidSizeOfMaxUnidirectionalCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int s_maxValidSizeOfMaxUnidirectionalCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private const int s_minValidSizeOfMaxBidirectionalCapsuleValue = VariableLengthIntegerHelper.MinimumEncodedLength;
    private const int s_maxValidSizeOfMaxBidirectionalCapsuleValue = VariableLengthIntegerHelper.MaximumEncodedLength + 1;

    private void WriteMaxDataCapsule(Stream stream, long dataSentLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxDataCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(dataSentLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    private void WriteBidirectionalStreamLimitCapsule(Stream stream, long bidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxBidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(bidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    private void WriteUnidirectionalStreamLimitCapsule(Stream stream, long unidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxUnidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(unidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    [Fact]
    public async Task SetUnidirectionalStreamCountLimitSendsCorrectCapsule()
    {
        using Barrier barrier = new(2);
        int expectedUnidirectionalStreamCountLimit = 1;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedMaxUnidirectionalStreams, bytesReadMaxUnidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_maxUnidirectionalStreamLimitCapsuleCode, capsuleCode);
            Assert.Equal(expectedUnidirectionalStreamCountLimit, receivedMaxUnidirectionalStreams);
            Assert.Equal(capsuleValueLength, bytesReadMaxUnidirectionalStreams);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SetBidirectionalStreamCountLimitSendsCorrectCapsule()
    {
        using Barrier barrier = new(2);
        int expectedBidirectionalStreamCountLimit = 2;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedMaxBidirectionalStreams, bytesReadMaxBidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_maxBidirectionalStreamLimitCapsuleCode, capsuleCode);
            Assert.Equal(expectedBidirectionalStreamCountLimit, receivedMaxBidirectionalStreams);
            Assert.Equal(capsuleValueLength, bytesReadMaxBidirectionalStreams);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SetDataSentLimitSendsCorrectCapsule()
    {
        using Barrier barrier = new(2);
        int expectedDataSentLimit = 1024;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            var (receivedDataSentLimit, bytesReadDataSentLimit) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_maxDataCapsuleCode, capsuleCode);
            Assert.Equal(expectedDataSentLimit, receivedDataSentLimit);
            Assert.Equal(capsuleValueLength, bytesReadDataSentLimit);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetDataSentLimitForPeerAsync(expectedDataSentLimit);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SetUnidirectionalStreamCountLimitUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            int expectedUnidirectionalStreamCountLimit = 1024;
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);

            Assert.Equal(expectedUnidirectionalStreamCountLimit, session.UnidirectionalStreamCountLimitForPeer);

            barrier.SignalAndWait();
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SetBidirectionalStreamCountLimitUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            int expectedBidirectionalStreamCountLimit = 1024;
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);

            Assert.Equal(expectedBidirectionalStreamCountLimit, session.BidirectionalStreamCountLimitForPeer);

            barrier.SignalAndWait();
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SetDataSentLimitUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            int expectedDataSentLimit = 1024;
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            await session.SetDataSentLimitForPeerAsync(expectedDataSentLimit);

            Assert.Equal(expectedDataSentLimit, session.DataSentLimitForPeer);

            barrier.SignalAndWait();
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task SendAllCapsuleTypesSendsAndReceivesCorrectCapsules()
    {
        using Barrier barrier = new(2);
        int expectedUnidirectionalStreamCountLimit = 11;
        int expectedBidirectionalStreamCountLimit = 22;
        int expectedDataSentLimit = 3333;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

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
            var (receivedDataSentLimit, bytesReadDataSentLimit) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ConnectStream);

            Assert.Equal(s_maxUnidirectionalStreamLimitCapsuleCode, capsuleCode1);
            Assert.Equal(expectedUnidirectionalStreamCountLimit, receivedUnidirectionalStreamCountLimit);
            Assert.Equal(capsuleValueLength1, bytesReadUnidirectional);

            Assert.Equal(s_maxBidirectionalStreamLimitCapsuleCode, capsuleCode2);
            Assert.Equal(expectedBidirectionalStreamCountLimit, receivedBidirectionalStreamCountLimit);
            Assert.Equal(capsuleValueLength2, bytesReadBidirectional);

            Assert.Equal(s_maxDataCapsuleCode, capsuleCode3);
            Assert.Equal(expectedDataSentLimit, receivedDataSentLimit);
            Assert.Equal(capsuleValueLength3, bytesReadDataSentLimit);

            barrier.SignalAndWait();
        });

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            await session.SetUnidirectionalStreamCountLimitForPeerAsync(expectedUnidirectionalStreamCountLimit);
            await session.SetBidirectionalStreamCountLimitForPeerAsync(expectedBidirectionalStreamCountLimit);
            await session.SetDataSentLimitForPeerAsync(expectedDataSentLimit);

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReceiveAllCapsuleTypesUpdatesSessionProperties()
    {
        using Barrier barrier = new(2);
        int expectedUnidirectionalStreamCountLimit = 123;
        int expectedBidirectionalStreamCountLimit = 456;
        int expectedDataSentLimit = 7890;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            SpinWait.SpinUntil(() => expectedUnidirectionalStreamCountLimit == session.UnidirectionalStreamCountLimitProvidedByPeer || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);
            SpinWait.SpinUntil(() => expectedBidirectionalStreamCountLimit == session.BidirectionalStreamCountLimitProvidedByPeer || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);
            SpinWait.SpinUntil(() => expectedDataSentLimit == session.DataSentLimitProvidedByPeer || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(expectedUnidirectionalStreamCountLimit, session.UnidirectionalStreamCountLimitProvidedByPeer);
            Assert.Equal(expectedBidirectionalStreamCountLimit, session.BidirectionalStreamCountLimitProvidedByPeer);
            Assert.Equal(expectedDataSentLimit, session.DataSentLimitProvidedByPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            WriteUnidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedUnidirectionalStreamCountLimit);
            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedBidirectionalStreamCountLimit);
            WriteMaxDataCapsule(serverSession.ConnectStream, expectedDataSentLimit);

            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReceiveUnidirectionalStreamLimitCapsuleUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);
        int expectedLimit = 123;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            SpinWait.SpinUntil(() => session.UnidirectionalStreamCountLimitProvidedByPeer == expectedLimit || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(expectedLimit, session.UnidirectionalStreamCountLimitProvidedByPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            WriteUnidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit);

            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReceiveBidirectionalStreamLimitCapsuleUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);
        int expectedLimit = 456;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            SpinWait.SpinUntil(() => session.BidirectionalStreamCountLimitProvidedByPeer == expectedLimit || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(expectedLimit, session.BidirectionalStreamCountLimitProvidedByPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit);

            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task ReceiveMaxDataCapsuleUpdatesSessionProperty()
    {
        using Barrier barrier = new(2);
        int expectedLimit = 7890;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            SpinWait.SpinUntil(() => session.DataSentLimitProvidedByPeer == expectedLimit || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(expectedLimit, session.DataSentLimitProvidedByPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            WriteMaxDataCapsule(serverSession.ConnectStream, expectedLimit);

            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10_001)] // This should trigger special handling of long unknown capsules
    public async Task ReceiveUnknownCapsuleOnConnectStream(long capsuleValueSize)
    {
        using Barrier barrier = new(2);

        int expectedLimit = 42;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });
            // Assert the valid capsule after the unknown one is received
            SpinWait.SpinUntil(() => session.BidirectionalStreamCountLimitProvidedByPeer == expectedLimit || session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);
            Assert.Equal(expectedLimit, session.BidirectionalStreamCountLimitProvidedByPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_unknownCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, capsuleValueSize);
            await serverSession.ConnectStream.WriteAsync(new byte[capsuleValueSize]);
            WriteBidirectionalStreamLimitCapsule(serverSession.ConnectStream, expectedLimit); // Write a valid capsule after the unknown one
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(s_minValidSizeOfMaxDataCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfMaxDataCapsuleValue + 1)]
    public async Task ReceiveMaxDataCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_maxDataCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            try
            {
                serverSession.ConnectStream.Write(new byte[invalidLength]);
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.StreamAborted)
            {
                // The client may have already detected the invalid capsule and aborted the stream
            }
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }


    [Theory]
    [InlineData(s_minValidSizeOfMaxUnidirectionalCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfMaxUnidirectionalCapsuleValue + 1)]
    public async Task ReceiveMaxUnidirectionalStreamLimitCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, 10000);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_maxUnidirectionalStreamLimitCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            try
            {
                serverSession.ConnectStream.Write(new byte[invalidLength]);
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.StreamAborted)
            {
                // The client may have already detected the invalid capsule and aborted the stream
            }
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Theory]
    [InlineData(s_minValidSizeOfMaxBidirectionalCapsuleValue - 1)]
    [InlineData(s_maxValidSizeOfMaxBidirectionalCapsuleValue + 1)]
    public async Task ReceiveMaxBidirectionalStreamLimitCapsuleWithInvalidValueClosesSession(int invalidLength)
    {
        using Barrier barrier = new(2);

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0
            });

            // Wait for session to be closed due to invalid capsule
            SpinWait.SpinUntil(() => session.State != WebTransportSessionState.Open, TestTimeoutInMilliseconds);

            Assert.Equal(WebTransportSessionState.AbortedLocally, session.State);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, s_maxBidirectionalStreamLimitCapsuleCode);
            VariableLengthIntegerStreamHelper.Write(serverSession.ConnectStream, invalidLength);
            try
            {
                serverSession.ConnectStream.Write(new byte[invalidLength]);
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.StreamAborted)
            {
                // The client may have already detected the invalid capsule and aborted the stream
            }
            await serverSession.ConnectStream.FlushAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }

    [Fact]
    public async Task CreationOptionsOfLimitsSetTheirRespectiveProperties()
    {
        using Barrier barrier = new(2);

        int expectedBidirectionalStreamsCountLimitForPeer = 10;
        int expectedUnidirectionalStreamsCountLimitForPeer = 11;
        int expectedDataSentLimitForPeer = 111;

        Task clientTask = Task.Run(async () =>
        {
            await using WebTransportSession session = await ClientWebTransportSession.ConnectAsync(new WebTransportSessionCreationOptions
            {
                Uri = _webTransportServer.Address,
                HttpMessageInvoker = _client,
                DefaultStreamErrorCode = 0,

                InitialBidirectionalStreamCountLimitForPeer = expectedBidirectionalStreamsCountLimitForPeer,
                InitialUnidirectionalStreamCountLimitForPeer = expectedUnidirectionalStreamsCountLimitForPeer,
                InitialDataSentLimitForPeer = expectedDataSentLimitForPeer,
            });

            Assert.Equal(expectedBidirectionalStreamsCountLimitForPeer, session.BidirectionalStreamCountLimitForPeer);
            Assert.Equal(expectedUnidirectionalStreamsCountLimitForPeer, session.UnidirectionalStreamCountLimitForPeer);
            Assert.Equal(expectedDataSentLimitForPeer, session.DataSentLimitForPeer);

            barrier.SignalAndWait();
        });

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await _webTransportServer.AcceptHttpConnectionAndWebTransportServerSessionAsync();

            barrier.SignalAndWait();
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeoutInMilliseconds);
    }
}
