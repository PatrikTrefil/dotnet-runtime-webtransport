// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO;
using System.Net.Test.Common;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.WebTransport.Functional.Tests;

internal static class CapsuleHelper
{
    public const long s_closeSessionCapsuleCode = 0x2843;
    public const long s_drainSessionCapsuleCode = 0x78ae;
    public const long s_maxUnidirectionalStreamLimitCapsuleCode = 0x190B4D40;
    public const long s_maxBidirectionalStreamLimitCapsuleCode = 0x190B4D3F;
    public const long s_maxDataCapsuleCode = 0x190B4D3D;
    public const long s_unknownCapsuleCode = 0x12345678;

    public static void WriteDrainCapsule(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_drainSessionCapsuleCode);
        VariableLengthIntegerStreamHelper.Write(stream, 0);
    }

    public static async Task ReadDrainCapsuleAsync(Stream stream)
    {
        var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        Assert.Equal(s_drainSessionCapsuleCode, capsuleCode);
        Assert.Equal(0, capsuleValueLength);
    }

    public static void WriteMaxDataCapsule(Stream stream, long dataSentLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxDataCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(dataSentLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static async Task<long> ReadMaxDataCapsule(Stream stream)
    {
        var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (receivedDataSentLimit, bytesReadDataSentLimit) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        Assert.Equal(s_maxDataCapsuleCode, capsuleCode);
        Assert.Equal(capsuleValueLength, bytesReadDataSentLimit);

        return receivedDataSentLimit;
    }

    public static void WriteUnidirectionalStreamLimitCapsule(Stream stream, long unidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxUnidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(unidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static async Task<long> ReadUnidirectionalStreamLimitCapsule(Stream stream)
    {
        var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (receivedMaxUnidirectionalStreams, bytesReadMaxUnidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        Assert.Equal(s_maxUnidirectionalStreamLimitCapsuleCode, capsuleCode);
        Assert.Equal(capsuleValueLength, bytesReadMaxUnidirectionalStreams);

        return receivedMaxUnidirectionalStreams;
    }

    public static void WriteBidirectionalStreamLimitCapsule(Stream stream, long bidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxBidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(bidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static async Task<long> ReadBidirectionalStreamLimitCapsule(Stream stream)
    {
        var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (receivedMaxBidirectionalStreams, bytesReadMaxBidirectionalStreams) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        Assert.Equal(s_maxBidirectionalStreamLimitCapsuleCode, capsuleCode);
        Assert.Equal(capsuleValueLength, bytesReadMaxBidirectionalStreams);

        return receivedMaxBidirectionalStreams;
    }

    public static void WriteCloseSessionCapsule(Stream stream, byte[] applicationErrorMessage, uint applicationErrorCode)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_closeSessionCapsuleCode);

        Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];

        VariableLengthIntegerStreamHelper.Write(stream, applicationErrorMessage.Length + applicationErrorCodeBuffer.Length);

        BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, applicationErrorCode);

        stream.Write(applicationErrorCodeBuffer);

        if (applicationErrorMessage.Length > 0) // If the message is empty, the client could have already closed the stream and therefore the write would fail.
        {
            stream.Write(applicationErrorMessage);
        }
    }

    public static async Task<(uint ApplicationErrorCode , byte[] ApplicationErrorMessage)> ReadCloseSessionCapsule(Stream stream)
    {
        var (capsuleCode, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        var (capsuleValueLength, _) = await VariableLengthIntegerStreamHelper.ReadAsync(stream);

        Memory<byte> errorCodeBuffer = new byte[4];
        await stream.ReadExactlyAsync(errorCodeBuffer);
        uint receivedApplicationErrorCode = BinaryPrimitives.ReadUInt32BigEndian(errorCodeBuffer.Span);

        byte[] messageBuffer = new byte[capsuleValueLength - sizeof(uint)];
        await stream.ReadExactlyAsync(messageBuffer);

        Assert.Equal(s_closeSessionCapsuleCode, capsuleCode);

        return (receivedApplicationErrorCode, messageBuffer);
    }
}
