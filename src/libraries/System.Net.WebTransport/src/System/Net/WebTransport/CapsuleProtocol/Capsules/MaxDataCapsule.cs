// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

internal sealed class MaxDataCapsule : Capsule
{
    public const long s_code = 0x190B4D3D;
    public override long Code => s_code;

    public long MaxData { get; }
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x99, 0xB, 0x4D, 0x3D];

    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;

    protected override int ValueLength => VariableLengthIntegerHelper.GetByteCount(MaxData);

    public MaxDataCapsule(long maxData)
    {
        MaxData = maxData;
    }

    public override void ProcessReceived(MsQuicWebTransportSession session)
    {
        if (session.DataSentLimitProvidedByPeer > MaxData)
        {
            throw new CapsuleProtocolException("Peer tried to lower the data limit.");
        }
        session.DataSentLimitProvidedByPeer = MaxData;
    }

    /// <exception cref="CapsuleProtocolException">When the received length does not match the payload length</exception>
    public static MaxDataCapsule Deserialize(ReadOnlySpan<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer, out long maxData, out int maxDataBytesRead);

        if (!isReadSuccessful || buffer.Length != maxDataBytesRead)
        {
            throw new CapsuleProtocolException("Received invalid capsule data");
        }

        return new MaxDataCapsule(maxData);
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

        bool isMaxDataValueWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), MaxData, out int _);
        Debug.Assert(isMaxDataValueWriteSuccessful);
    }
}
