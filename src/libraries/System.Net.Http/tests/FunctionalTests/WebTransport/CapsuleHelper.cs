// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO;
using System.Net.Test.Common;

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
    public static void WriteMaxDataCapsule(Stream stream, long dataSentLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxDataCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(dataSentLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static void WriteBidirectionalStreamLimitCapsule(Stream stream, long bidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxBidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(bidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static void WriteUnidirectionalStreamLimitCapsule(Stream stream, long unidirectionalStreamLimit)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_maxUnidirectionalStreamLimitCapsuleCode);
        Span<byte> valueBuffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        int valueSizeInBytes = VariableLengthIntegerHelper.EncodeVariableLengthInteger(unidirectionalStreamLimit, valueBuffer);
        VariableLengthIntegerStreamHelper.Write(stream, valueSizeInBytes);
        stream.Write(valueBuffer.Slice(0, valueSizeInBytes));
    }

    public static void WriteCloseSessionCapsule(Stream stream, byte[] expectedApplicationErrorMessage, uint expectedApplicationErrorCode)
    {
        VariableLengthIntegerStreamHelper.Write(stream, s_closeSessionCapsuleCode);
        Span<byte> applicationErrorCodeBuffer = stackalloc byte[4];
        VariableLengthIntegerStreamHelper.Write(stream, expectedApplicationErrorMessage.Length + applicationErrorCodeBuffer.Length);
        BinaryPrimitives.WriteUInt32BigEndian(applicationErrorCodeBuffer, expectedApplicationErrorCode);
        stream.Write(applicationErrorCodeBuffer);
        stream.Write(expectedApplicationErrorMessage);
    }
}
