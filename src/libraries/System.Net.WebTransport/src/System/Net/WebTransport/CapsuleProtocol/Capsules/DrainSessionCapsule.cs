// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

internal sealed class DrainSessionCapsule : Capsule
{
    private static DrainSessionCapsule? s_instance;
    public static DrainSessionCapsule Instance
    {
        get
        {
            s_instance ??= new DrainSessionCapsule();
            return s_instance;
        }
    }
    public const long s_code = 0x78ae;
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x80, 0x0, 0x78, 0xAE];
    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;

    protected override int ValueLength => 0;

    public override long Code => s_code;

    private DrainSessionCapsule() { }

    public override void ProcessReceived(MsQuicWebTransportSession session)
    {
        session.ReceiveDrain();
    }

    public static DrainSessionCapsule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != 0)
        {
            throw new CapsuleProtocolException("Invalid capsule data received");
        }
        return new DrainSessionCapsule();
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

        bool isValueLengthWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), ValueLength, out int _);
        Debug.Assert(isValueLengthWriteSuccessful);
    }
}
