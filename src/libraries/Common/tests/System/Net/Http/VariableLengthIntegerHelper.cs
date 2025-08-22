// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Diagnostics;

namespace System.Net.Test.Common;

internal static class VariableLengthIntegerHelper
{
    public const int MinimumEncodedLength = 1;
    public const int MaximumEncodedLength = 8;

    private const long VarIntMax = (1L << 62) - 1;
    public static int EncodeVariableLengthInteger(long longToEncode, Span<byte> buffer)
    {
        Debug.Assert(longToEncode >= 0);
        Debug.Assert(longToEncode <= VarIntMax);

        const uint OneByteLimit = (1U << 6) - 1;
        const uint TwoByteLimit = (1U << 14) - 1;
        const uint FourByteLimit = (1U << 30) - 1;

        if (longToEncode < OneByteLimit)
        {
            buffer[0] = (byte)longToEncode;
            return 1;
        }
        else if (longToEncode < TwoByteLimit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)((uint)longToEncode | 0x4000u));
            return 2;
        }
        else if (longToEncode < FourByteLimit)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)longToEncode | 0x80000000);
            return 4;
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, (ulong)longToEncode | 0xC000000000000000);
            return 8;
        }
    }
    public static bool TryDecodeVariableLengthInteger(ReadOnlySpan<byte> buffer, out long value, out int bytesRead)
    {
        const byte LengthMask = 0xC0;
        const byte LengthOneByte = 0x00;
        const byte LengthTwoByte = 0x40;
        const byte LengthFourByte = 0x80;
        const byte LengthEightByte = 0xC0;

        const uint TwoByteSubtract = 0x4000;
        const uint FourByteSubtract = 0x80000000;
        const ulong EightByteSubtract = 0xC000000000000000;

        if (buffer.Length != 0)
        {
            byte firstByte = buffer[0];

            switch (firstByte & LengthMask)
            {
                case LengthOneByte:
                    value = firstByte;
                    bytesRead = 1;
                    return true;
                case LengthTwoByte:
                    if (BinaryPrimitives.TryReadUInt16BigEndian(buffer, out ushort serializedShort))
                    {
                        value = serializedShort - TwoByteSubtract;
                        bytesRead = 2;
                        return true;
                    }
                    break;
                case LengthFourByte:
                    if (BinaryPrimitives.TryReadUInt32BigEndian(buffer, out uint serializedInt))
                    {
                        value = serializedInt - FourByteSubtract;
                        bytesRead = 4;
                        return true;
                    }
                    break;
                default: // LengthEightByte
                    Debug.Assert((firstByte & LengthMask) == LengthEightByte);
                    if (BinaryPrimitives.TryReadUInt64BigEndian(buffer, out ulong serializedLong))
                    {
                        value = (long)(serializedLong - EightByteSubtract);
                        Debug.Assert(value >= 0 && value <= VarIntMax, "Serialized values are within [0, 2^62).");

                        bytesRead = 8;
                        return true;
                    }
                    break;
            }
        }

        value = 0;
        bytesRead = 0;
        return false;
    }
}
