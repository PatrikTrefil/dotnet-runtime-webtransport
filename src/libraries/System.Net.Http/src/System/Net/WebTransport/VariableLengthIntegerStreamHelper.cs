// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;

namespace System.Net.WebTransport;

// TODO: this could be merged into VariableLengthIntegerHelper

internal static class VariableLengthIntegerStreamHelper
{
    public const int MaximumEncodedLength = 8;

    private const byte LengthMask = 0xC0;
    private const byte InitialOneByteLengthMask = 0x00;
    private const byte InitialTwoByteLengthMask = 0x40;
    private const byte InitialFourByteLengthMask = 0x80;
    private const byte InitialEightByteLengthMask = 0xC0;

    private const uint TwoByteLengthMask = 0x4000;
    private const uint FourByteLengthMask = 0x80000000;
    private const ulong EightByteLengthMask = 0xC000000000000000;
    /// <summary>
    /// Reads exactly one variable length integer from the <paramref name="stream"/>.
    /// </summary>
    /// <returns>the parsed integer</returns>
    /// <exception cref="ArgumentException">When the <paramref name="stream"/> is empty</exception>
    public static long Read(Stream stream) => Read(stream, out int _);

    // TODO: should the read/write methods be async? maybe use ValueTask to reduce allocations?
    /// <summary>
    /// Reads exactly one variable length integer from the <paramref name="stream"/>.
    /// </summary>
    /// <param name="bytesRead">Number of bytes that has been read from the stream, i.e. how many bytes were used to encode the parsed value.</param>
    /// <returns>the parsed integer</returns>
    /// <exception cref="ArgumentException">When the <paramref name="stream"/> is empty</exception>
    public static long Read(Stream stream, out int bytesRead)
    {
        int firstByte = stream.ReadByte();
        if (firstByte == -1)
        {
            throw new ArgumentException("Stream is empty");
        }

        switch (firstByte & LengthMask)
        {
            case InitialOneByteLengthMask:
                bytesRead = 1;
                return firstByte;
            case InitialTwoByteLengthMask:
                Span<byte> twoByteBuffer = stackalloc byte[2];
                stream.ReadExactly(twoByteBuffer);
                if (BinaryPrimitives.TryReadUInt16BigEndian(twoByteBuffer, out ushort serializedShort))
                {
                    bytesRead = 2;
                    return serializedShort - TwoByteLengthMask;
                }
                break;
            case InitialFourByteLengthMask:
                Span<byte> fourByteBuffer = stackalloc byte[4];
                stream.ReadExactly(fourByteBuffer);
                if (BinaryPrimitives.TryReadUInt32BigEndian(fourByteBuffer, out uint serializedInt))
                {
                    bytesRead = 4;
                    return serializedInt - FourByteLengthMask;
                }
                break;
            default: // InitialEightByteLengthMask
                Debug.Assert((firstByte & LengthMask) == InitialEightByteLengthMask);
                Span<byte> eightByteBuffer = stackalloc byte[8];
                stream.ReadExactly(eightByteBuffer);
                if (BinaryPrimitives.TryReadUInt64BigEndian(eightByteBuffer, out ulong serializedLong))
                {
                    bytesRead = 8;
                    return (long)(serializedLong - EightByteLengthMask);
                }
                break;
        }
        throw new Exception("Should be unreachable");
    }
    public static void Write(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[MaximumEncodedLength];
        bool isSuccess = System.Net.Http.VariableLengthIntegerHelper.TryWrite(buffer, value, out int bytesWritten);
        Debug.Assert(isSuccess, $"Should always succeed because the {nameof(buffer)} has length of {nameof(MaximumEncodedLength)}");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}
