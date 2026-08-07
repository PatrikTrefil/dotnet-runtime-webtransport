// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

internal sealed class CloseSessionCapsule : Capsule
{
    public const long s_code = 0x2843;
    public override long Code => s_code;
    private static readonly byte[] s_capsuleCodeEncodedAsVariableLengthInteger = [0x68, 0x43];
    protected override byte[] CapsuleCodeEncodedAsVariableLengthInteger => s_capsuleCodeEncodedAsVariableLengthInteger;
    private static readonly Encoding _encoding = Encoding.UTF8;

    private const int s_applicationErrorCodeSize = sizeof(uint);
    private const int s_applicationErrorMessageOffset = s_applicationErrorCodeSize;
    private const int s_applicationErrorMessageLengthInBytesLimit = 1024;

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
