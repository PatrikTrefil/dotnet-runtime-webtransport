// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Test.Common;

namespace System.Net.WebTransport;

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

    /// <summary>
    /// Reads exactly one variable length integer from the <paramref name="stream"/>.
    /// </summary>
    /// <param name="bytesRead">Number of bytes that has been read from the stream, i.e. how many bytes were used to encode the parsed value.</param>
    /// <param name="stream">Stream to read from</param>
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
                ushort serializedShort = BinaryPrimitives.ReadUInt16BigEndian(twoByteBuffer);
                bytesRead = 2;
                return serializedShort - TwoByteLengthMask;
            case InitialFourByteLengthMask:
                Span<byte> fourByteBuffer = stackalloc byte[4];
                stream.ReadExactly(fourByteBuffer);
                uint serializedInt = BinaryPrimitives.ReadUInt32BigEndian(fourByteBuffer);
                bytesRead = 4;
                return serializedInt - FourByteLengthMask;
            default: // InitialEightByteLengthMask
                Debug.Assert((firstByte & LengthMask) == InitialEightByteLengthMask);
                Span<byte> eightByteBuffer = stackalloc byte[8];
                stream.ReadExactly(eightByteBuffer);
                ulong serializedLong = BinaryPrimitives.ReadUInt64BigEndian(eightByteBuffer);
                bytesRead = 8;
                return (long)(serializedLong - EightByteLengthMask);
        }
    }
    public static void Write(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[MaximumEncodedLength];
        int bytesWritten = VariableLengthIntegerHelper.EncodeVariableLengthInteger(value, buffer);
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}
