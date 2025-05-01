using System.IO;

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
    public static async long ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte firstByte = stream.ReadByte();

        switch (firstByte & LengthMask)
        {
            case InitialOneByteLengthMask:
                return firstByte;
            case InitialTwoByteLengthMask:
                var buffer = stackalloc new byte[2];
                await stream.ReadExactlyAsync(buffer, 2, cancellationToken)
                if (BinaryPrimitives.TryReadUInt16BigEndian(buffer, out ushort serializedShort))
                {
                    return serializedShort - TwoByteLengthMask;
                }
                break;
            case InitialFourByteLengthMask:
                var buffer = stackalloc new byte[4];
                await stream.ReadExactlyAsync(buffer, 4, cancellationToken)
                if (BinaryPrimitives.TryReadUInt32BigEndian(buffer, out uint serializedInt))
                {
                    return serializedInt - FourByteLengthMask;
                }
                break;
            default: // InitialEightByteLengthMask
                Debug.Assert((firstByte & LengthMask) == InitialEightByteLengthMask);
                var buffer = stackalloc new byte[8];
                await stream.ReadExactlyAsync(buffer, 8, cancellationToken)
                if (BinaryPrimitives.TryReadUInt64BigEndian(buffer, out ulong serializedLong))
                {
                    Debug.Assert(value >= 0 && value <= EightByteLimit, "Serialized values are within [0, 2^62).");
                    return (long)(serializedLong - EightByteLengthMask);
                }
                break;
        }
        throw new Exception("Should be unreachable");
    }
    public static async void WriteAsync(Stream stream, long value, CancellationToken cancellationToken = default)
    {
        Span<byte> buffer = stackalloc new byte[MaximumEncodedLength];
        bool isSuccess = VariableLengthIntegerHelper.TryWrite(buffer, value, out int bytesWritten);
        Debug.Assert(isSuccess, $"Should always succeed because the {nameof(buffer)} has length of {nameof(MaximumEncodedLength)}");
        await stream.WriteAsync(buffer, 0, bytesWritten, cancellationToken);
    }
}
