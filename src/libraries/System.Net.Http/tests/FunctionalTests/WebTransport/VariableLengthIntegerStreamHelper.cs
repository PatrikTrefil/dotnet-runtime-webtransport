// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Test.Common;
using System.Threading.Tasks;

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
    /// <param name="bytesRead">Number of bytes that has been read from the stream, i.e. how many bytes were used to encode the parsed value.</param>
    /// <param name="stream">Stream to read from</param>
    /// <returns>the parsed integer</returns>
    /// <exception cref="ArgumentException">When the <paramref name="stream"/> is empty</exception>
    public async static Task<(long Value, int BytesRead)> ReadAsync(Stream stream)
    {
        byte[] buffer = new byte[MaximumEncodedLength];

        await stream.ReadExactlyAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false);

        int firstByte = buffer[0];
        switch (firstByte & LengthMask)
        {
            case InitialOneByteLengthMask:
                return (firstByte, 1);
            case InitialTwoByteLengthMask:
                await stream.ReadExactlyAsync(buffer.AsMemory(1, 1)).ConfigureAwait(false);
                ushort serializedShort = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
                return (serializedShort - TwoByteLengthMask, 2);
            case InitialFourByteLengthMask:
                await stream.ReadExactlyAsync(buffer.AsMemory(1, 3)).ConfigureAwait(false);
                uint serializedInt = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
                return (serializedInt - FourByteLengthMask, 4);
            default: // InitialEightByteLengthMask
                Debug.Assert((firstByte & LengthMask) == InitialEightByteLengthMask);
                await stream.ReadExactlyAsync(buffer.AsMemory(1, 7)).ConfigureAwait(false);
                ulong serializedLong = BinaryPrimitives.ReadUInt64BigEndian(buffer);
                return ((long)(serializedLong - EightByteLengthMask), 8);
        }
    }
    public static void Write(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[MaximumEncodedLength];
        int bytesWritten = VariableLengthIntegerHelper.EncodeVariableLengthInteger(value, buffer);
        stream.Write(buffer.Slice(0, bytesWritten));
    }
}
