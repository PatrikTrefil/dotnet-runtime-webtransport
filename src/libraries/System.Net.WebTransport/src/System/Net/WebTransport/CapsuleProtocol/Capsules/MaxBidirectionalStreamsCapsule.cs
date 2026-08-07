// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

internal sealed class MaxBidirectionalStreamsCapsule : Capsule
{
    public const long s_code = 0x190B4D3F;
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x99, 0xB, 0x4D, 0x3F];
    public override long Code => s_code;
    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;

    public long MaxBidirectionalStreams { get; }

    protected override int ValueLength => VariableLengthIntegerHelper.GetByteCount(MaxBidirectionalStreams);

    public MaxBidirectionalStreamsCapsule(long maxBidirectionalStreams)
    {
        MaxBidirectionalStreams = maxBidirectionalStreams;
    }

    public override void ProcessReceived(MsQuicWebTransportSession session)
    {
        if (session.BidirectionalStreamCountLimitProvidedByPeer > MaxBidirectionalStreams)
        {
            throw new CapsuleProtocolException("Peer tried to lower the bidirectional stream limit.");
        }
        try
        {
            session.BidirectionalStreamCountLimitProvidedByPeer = MaxBidirectionalStreams;
        } catch (ArgumentException e)
        {
            throw new CapsuleProtocolException($"Unsupported maximum bidirectional stream count limit received ({MaxBidirectionalStreams}).", e);
        }
    }

    /// <exception cref="CapsuleProtocolException">When the received length does not match the payload length</exception>
    public static MaxBidirectionalStreamsCapsule Deserialize(ReadOnlySpan<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer, out long maxBidirectionalStreams, out int maxBidirectionalStreamsBytesRead);

        if (!isReadSuccessful || buffer.Length != maxBidirectionalStreamsBytesRead)
        {
            throw new CapsuleProtocolException("Received invalid capsule data");
        }

        return new MaxBidirectionalStreamsCapsule(maxBidirectionalStreams);
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

        bool isMaxBidirectionalStreamsValueWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), MaxBidirectionalStreams, out int _);
        Debug.Assert(isMaxBidirectionalStreamsValueWriteSuccessful);
    }
}
