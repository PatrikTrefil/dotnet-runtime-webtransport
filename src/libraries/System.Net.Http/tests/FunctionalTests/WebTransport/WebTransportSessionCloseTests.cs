// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;
using System.Net.Http;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace System.Net.WebTransport.Functional.Tests;

public sealed class WebTransportSessionCloseTests : WebTransportTestBase
{
    public WebTransportSessionCloseTests(ITestOutputHelper output) : base(output) { }
    private const int TestTimeout = 200_000_00;
    private const long CloseSessionCapsuleCode = 0x2843;

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(ErrorMessagesAsParameters))]
    public async Task SessionCloseAsyncSendsCorrectCapsule(byte[] expectedApplicationErrorMessage)
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        uint expectedApplicationErrorCode = 1;

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);

            var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ControlStream);

            var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(serverSession.ControlStream);

            Memory<byte> errorCodeBuffer = new byte[4];
            await serverSession.ControlStream.ReadExactlyAsync(errorCodeBuffer); // TODO: use async reads everywhere
            uint receivedApplicationErrorCode = BinaryPrimitives.ReadUInt32BigEndian(errorCodeBuffer.Span);

            Memory<byte> messageBuffer = new byte[expectedApplicationErrorMessage.Length];
            await serverSession.ControlStream.ReadExactlyAsync(messageBuffer);

            Assert.Equal(CloseSessionCapsuleCode, capsuleCode);
            Assert.Equal(expectedApplicationErrorCode, receivedApplicationErrorCode);
            Assert.Equal(capsuleValueLength, errorCodeBuffer.Length + messageBuffer.Length);
            Assert.Equal(expectedApplicationErrorMessage, messageBuffer);
        });

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);
            await session.CloseAsync(expectedApplicationErrorCode, expectedApplicationErrorMessage);
            await Task.WhenAll(serverTask); // prevent client from closing connect stream
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalTheory(nameof(IsWebTransportSupported))]
    [MemberData(nameof(ErrorMessagesAsParameters))]
    public async Task ClientClosesSessionAfterReceivingCloseSessionCapsule(byte[] expectedApplicationErrorMessage)
    {
        using var listener = new TestUtilities.TestEventListener(
    Console.Out,
    new[]
        {
        "Private.InternalDiagnostics.System.Net.Http",
        }
    );
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();
        uint expectedApplicationErrorCode = 1;

        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            // TODO: do I just wait for x seconds here to make sure it has been received?
            await Task.Delay(2000);

            Assert.Equal(WebTransportSessionState.Closed, session.State); // TODO: this should be volatile, otherwise it sometimes fails
            Assert.Equal(Encoding.UTF8.GetString(expectedApplicationErrorMessage), session.CloseStatusDescription);
            Assert.Equal(expectedApplicationErrorCode, session.CloseStatusCode);
        });
        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            VariableLengthIntegerStreamHelper.Write(serverSession.ControlStream, CloseSessionCapsuleCode);
            Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];
            VariableLengthIntegerStreamHelper.Write(serverSession.ControlStream, expectedApplicationErrorMessage.Length + applicationErrorCodeBuffer.Length);
            BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, expectedApplicationErrorCode);
            serverSession.ControlStream.Write(applicationErrorCodeBuffer);
            serverSession.ControlStream.Write(expectedApplicationErrorMessage);
            await Task.WhenAll(clientTask);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }

    [ConditionalFact(nameof(IsWebTransportSupported))]
    public async Task ServerClosesControlStream()
    {
        using Http3LoopbackServer server = CreateHttp3LoopbackServer();

        Task serverTask = Task.Run(async () =>
        {
            await using WebTransportServerSession serverSession = await WebTransportLoopbackServer.EstablishWebTransportServerSessionAsync(server);
            serverSession.ControlStream.CompleteWrites();
        });
        Task clientTask = Task.Run(async () =>
        {
            using HttpClient client = CreateHttpClient();
            await using WebTransportSession session = await WebTransportSession.ConnectAsync(server.Address, client);

            await Task.WhenAll(serverTask);
            // TODO: maybe wait for a bit to ensure the client has had time to react?

            Assert.Equal(WebTransportSessionState.Closed, session.State); // TODO: this should be volatile
            Assert.Equal("", session.CloseStatusDescription);
            Assert.Equal(0, session.CloseStatusCode);
        });

        await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(TestTimeout);
    }
    private static readonly byte[][] _errorMessages = [
        ""u8.ToArray(),
        "test errror message"u8.ToArray(),

    ];
    private static readonly IEnumerable<object[]> _errorMessagesAsParameters = _errorMessages.Select(item => new object[] { item });
    public static IEnumerable<object[]> ErrorMessagesAsParameters => _errorMessagesAsParameters;
}
