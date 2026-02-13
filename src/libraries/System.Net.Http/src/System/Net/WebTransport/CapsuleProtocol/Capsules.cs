// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

// The capsule protocol can be found here https://datatracker.ietf.org/doc/html/rfc9297

internal abstract class Capsule
{
    public abstract long Code { get; }
    /// <summary>
    /// Length of the capsule when serialized in bytes.
    /// </summary>
    public int TotalLength => CapsuleCodeEncodedAsVariableLengthInteger.Length + VariableLengthIntegerHelper.GetByteCount(ValueLength) + ValueLength;
    protected abstract byte[] CapsuleCodeEncodedAsVariableLengthInteger { get; }
    /// <summary>
    /// Length of the Capsule Value.
    /// </summary>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc9297#name-capsule-format" />
    protected abstract int ValueLength { get; }
    /// <summary>
    /// Processes the capsule received from the peer.
    /// </summary>
    /// <param name="session">WebTransport session which received the session.</param>
    public abstract void ProcessReceived(MsQuicWebTransportSession session);

    /// <summary>
    ///  Serializes the capsule to the provided <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Buffer to which the capsule will be serialized.</param>
    /// <exception cref="ArgumentException">When the <paramref name="buffer"/> length is less than <see cref="TotalLength"/></exception>
    public abstract void Serialize(Span<byte> buffer);
}

internal sealed class CloseSessionCapsule : Capsule
{
    public const long s_code = 0x2843;
    public override long Code => s_code;
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x68, 0x43];
    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;
    private static readonly Encoding _encoding = Encoding.UTF8;

    private const int s_applicationErrorCodeSize = sizeof(uint);
    private const int s_applicationErrorMessageOffset = s_applicationErrorCodeSize;
    private const int s_applicationErrorMessageLengthInBytesLimit = 8192;

    protected override int ValueLength => s_applicationErrorCodeSize + ApplicationErrorMessage.Length;

    public uint ApplicationErrorCode { get; }
    /// <summary>
    /// Utf-8 encoded string containing the error message.
    /// </summary>
    public ReadOnlyMemory<byte> ApplicationErrorMessage { get; }
    public CloseSessionCapsule(uint applicationErrorCode, ReadOnlyMemory<byte> applicationErrorMessage)
    {
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }

    public override void ProcessReceived(MsQuicWebTransportSession session)
    {
        string applicationErrorMessageString = _encoding.GetString(ApplicationErrorMessage.Span);
        session.ReceiveClose(ApplicationErrorCode, applicationErrorMessageString);
    }

    public static CloseSessionCapsule Deserialize(ReadOnlySpan<byte> buffer)
    {
        long applicationErrorMessageLength = buffer.Length - s_applicationErrorMessageOffset;

        if (applicationErrorMessageLength > s_applicationErrorMessageLengthInBytesLimit)
        {
            throw new CapsuleProtocolException("Application error message length exceeded");
        }

        bool isReadOfApplicationErrorCodeSuccessful = BinaryPrimitives.TryReadUInt32BigEndian(buffer, out uint applicationErrorCode);
        if (!isReadOfApplicationErrorCodeSuccessful)
        {
            throw new CapsuleProtocolException("Invalid connect stream data received");
        }

        byte[] errorMessageBuffer = new byte[applicationErrorMessageLength];
        buffer.Slice(s_applicationErrorMessageOffset).CopyTo(errorMessageBuffer);

        return new CloseSessionCapsule(applicationErrorCode, errorMessageBuffer);
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

        bool isCapsuleLengthWriteSuccessful = VariableLengthIntegerHelper.TryWrite(buffer.Slice(currentOffset), ValueLength, out int bytesWrittenCapsuleLength);
        Debug.Assert(isCapsuleLengthWriteSuccessful);
        currentOffset += bytesWrittenCapsuleLength;

        bool isErrorCodeWriteSuccessful = BinaryPrimitives.TryWriteUInt32BigEndian(buffer.Slice(currentOffset), ApplicationErrorCode);
        Debug.Assert(isErrorCodeWriteSuccessful);
        currentOffset += s_applicationErrorCodeSize;

        ApplicationErrorMessage.Span.CopyTo(buffer.Slice(currentOffset));
    }
}

internal sealed class DrainSessionCapsule : Capsule
{
    private static DrainSessionCapsule s_instance = new();
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
