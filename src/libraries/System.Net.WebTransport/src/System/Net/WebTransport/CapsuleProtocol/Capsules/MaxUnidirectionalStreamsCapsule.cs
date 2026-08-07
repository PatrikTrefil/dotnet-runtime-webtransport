// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

internal sealed class MaxUnidirectionalStreamsCapsule : Capsule
{
    public const long s_code = 0x190B4D40;
    public override long Code => s_code;
    public long MaxUnidirectionalStreams { get; }
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x99, 0xB, 0x4D, 0x40];
    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;

    protected override int ValueLength => VariableLengthIntegerHelper.GetByteCount(MaxUnidirectionalStreams);

    public MaxUnidirectionalStreamsCapsule(long maxUnidirectionalStreams)
    {
        MaxUnidirectionalStreams = maxUnidirectionalStreams;
    }

    public override void ProcessReceived(MsQuicWebTransportSession session)
    {
        if (session.UnidirectionalStreamCountLimitProvidedByPeer > MaxUnidirectionalStreams)
        {
            throw new CapsuleProtocolException("Peer tried to lower the unidirectional stream limit.");
        }
        try {
            session.UnidirectionalStreamCountLimitProvidedByPeer = MaxUnidirectionalStreams;
        } catch (ArgumentException e)
        {
            throw new CapsuleProtocolException($"Unsupported maximum unidirectional stream count limit  received ({MaxUnidirectionalStreams}).", e);
        }
    }

    /// <exception cref="CapsuleProtocolException">When the received length does not match the payload length</exception>
    public static MaxUnidirectionalStreamsCapsule Deserialize(ReadOnlySpan<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer, out long maxUnidirectionalStream, out int maxUnidirectionalStreamsBytesRead);

        if (!isReadSuccessful || buffer.Length != maxUnidirectionalStreamsBytesRead)
        {
            throw new CapsuleProtocolException("Deserialization failed because of invalid capsule data - received length does not match the payload length");
        }

        return new MaxUnidirectionalStreamsCapsule(maxUnidirectionalStream);
    }

    public override void Serialize(Span<byte> buffer)
    {
        if (buffer.Length < TotalLength)
        {
            throw new ArgumentException("Buffer is not long enough.", nameof(buffer));
        }

        int currentOffset = 0;

        CapsuleCodeEncodedAsVariableLengthInteger.CopyTo(buffer);
        currentOffset += CapsuleCodeEncodedAsVariableLengthInteger.Length;

        bool isValueLengthWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), ValueLength, out int bytesWrittenValueLength);
        Debug.Assert(isValueLengthWriteSuccessful);
        currentOffset += bytesWrittenValueLength;

        bool isValueWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), MaxUnidirectionalStreams, out int _);
        Debug.Assert(isValueWriteSuccessful);
    }

}
