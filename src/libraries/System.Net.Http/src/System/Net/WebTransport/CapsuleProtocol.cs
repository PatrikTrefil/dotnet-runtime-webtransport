// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Threading;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

// TODO: reimplement serialization with buffers?

// The capsule protocol can be found here https://datatracker.ietf.org/doc/html/rfc9297

internal abstract class Capsule
{
    /// <summary>
    /// Processes the capsule received from the peer.
    /// </summary>
    /// <param name="session">WebTransport session which received the session.</param>
    public abstract void ProcessReceived(WebTransportSession session);
}

internal sealed class CloseSessionCapsule : Capsule
{
    public const long CapsuleCode = 0x2843;
    private static readonly Encoding _encoding = Encoding.UTF8;

    private const int s_applicationErrorCodeSize = sizeof(uint);
    private const int s_applicationErrorMessageOffset = s_applicationErrorCodeSize;
    private const int s_applicationErrorMessageLengthInBytesLimit = 8192;

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
    public override void ProcessReceived(WebTransportSession session)
    {
        string applicationErrorMessageString = _encoding.GetString(ApplicationErrorMessage.Span);
        session.ReceiveClose(ApplicationErrorCode, applicationErrorMessageString);
    }
    public static CloseSessionCapsule Deserialize(ReadOnlyMemory<byte> buffer)
    {
        if (buffer.Length < s_applicationErrorCodeSize)
        {
            throw new WebTransportException("Invalid connect stream data received");
        }

        long applicationErrorMessageLength = buffer.Length - s_applicationErrorMessageOffset;

        if (applicationErrorMessageLength > s_applicationErrorMessageLengthInBytesLimit)
        {
            throw new WebTransportException("Application error message length exceeded");
        }

        bool isReadOfApplicationErrorCodeSuccessful = BinaryPrimitives.TryReadUInt32BigEndian(buffer.Span, out uint applicationErrorCode);
        if (!isReadOfApplicationErrorCodeSuccessful)
        {
            throw new WebTransportException("Invalid connect stream data received");
        }

        byte[] errorMessageBuffer = new byte[applicationErrorMessageLength];
        buffer.Slice(s_applicationErrorMessageOffset).CopyTo(errorMessageBuffer);

        return new CloseSessionCapsule(applicationErrorCode, errorMessageBuffer);
    }
    public async Task SerializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] errorCodeBuffer = new byte[4];

        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        VariableLengthIntegerStreamHelper.Write(stream, errorCodeBuffer.Length + ApplicationErrorMessage.Length);

        bool isSuccess = BinaryPrimitives.TryWriteUInt32BigEndian(errorCodeBuffer, ApplicationErrorCode);
        Debug.Assert(isSuccess, $"{nameof(errorCodeBuffer)} should be large enough");
        await stream.WriteAsync(errorCodeBuffer, cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(ApplicationErrorMessage, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DrainSessionCapsule : Capsule
{
    public const long CapsuleCode = 0x78ae;
    public DrainSessionCapsule() { }
    public override void ProcessReceived(WebTransportSession session)
    {
        session.ReceiveDrain();
    }
    public static DrainSessionCapsule Deserialize(ReadOnlyMemory<byte> buffer)
    {
        if (buffer.Length != 0)
        {
            throw new WebTransportException("Invalid capsule data received");
        }
        return new DrainSessionCapsule();
    }
    [Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Keep the same interface for all capsule classes")]
    public void Serialize(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        VariableLengthIntegerStreamHelper.Write(stream, 0);
    }
}

internal sealed class MaxBidirectionalStreamsCapsule : Capsule
{
    public const long CapsuleCode = 0x190B4D3F;
    public long MaxBidirectionalStreams { get; }
    public MaxBidirectionalStreamsCapsule(long maxBidirectionalStreams)
    {
        MaxBidirectionalStreams = maxBidirectionalStreams;
    }
    public override void ProcessReceived(WebTransportSession session)
    {
        session.BidirectionalStreamCountLimitProvidedByPeer = MaxBidirectionalStreams;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxBidirectionalStreamsCapsule Deserialize(ReadOnlyMemory<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer.Span, out long maxBidirectionalStreams, out int bytesRead);

        if (!isReadSuccessful || buffer.Length != bytesRead)
        {
            throw new WebTransportException("Received invalid capsule data");
        }

        return new MaxBidirectionalStreamsCapsule(maxBidirectionalStreams);
    }
    public void Serialize(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = VariableLengthIntegerHelper.TryWrite(buffer, MaxBidirectionalStreams, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

internal sealed class MaxUnidirectionalStreamsCapsule : Capsule
{
    public const long CapsuleCode = 0x190B4D40;
    public long MaxUnidirectionalStreams { get; }
    public MaxUnidirectionalStreamsCapsule(long maxUnidirectionalStreams)
    {
        MaxUnidirectionalStreams = maxUnidirectionalStreams;
    }
    public override void ProcessReceived(WebTransportSession session)
    {
        session.UnidirectionalStreamCountLimitProvidedByPeer = MaxUnidirectionalStreams;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxUnidirectionalStreamsCapsule Deserialize(ReadOnlyMemory<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer.Span, out long maxUnidirectionalStream, out int maxUnidirectionalStreamsBytesRead);

        if (!isReadSuccessful || buffer.Length != maxUnidirectionalStreamsBytesRead)
        {
            throw new WebTransportException("Deserialization failed because of invalid capsule data - received length does not match the payload length");
        }

        return new MaxUnidirectionalStreamsCapsule(maxUnidirectionalStream);
    }
    public void Serialize(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = VariableLengthIntegerHelper.TryWrite(buffer, MaxUnidirectionalStreams, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

internal sealed class MaxDataCapsule : Capsule
{
    public const long CapsuleCode = 0x190B4D3D;
    public long MaxData { get; }
    public MaxDataCapsule(long maxData)
    {
        MaxData = maxData;
    }
    public override void ProcessReceived(WebTransportSession session)
    {
        session.MaxDataSentLimitProvidedByPeer = MaxData;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxDataCapsule Deserialize(ReadOnlyMemory<byte> buffer)
    {
        bool isReadSuccessful = VariableLengthIntegerHelper.TryRead(buffer.Span, out long maxData, out int maxDataBytesRead);

        if (!isReadSuccessful || buffer.Length != maxDataBytesRead)
        {
            throw new WebTransportException("Received invalid capsule data");
        }

        return new MaxDataCapsule(maxData);
    }
    public void Serialize(Stream stream)
    {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = VariableLengthIntegerHelper.TryWrite(buffer, MaxData, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

/// <summary>
/// Reads and processes capsules from a capsule stream.
/// </summary>
internal sealed class CapsuleConsumer
{
    private readonly Stream _capsuleStream;
    private readonly WebTransportSession _session;
    private readonly ArrayBuffer _buffer = new(initialSize: VariableLengthIntegerHelper.MaximumEncodedLength, usePool: true);
    public CapsuleConsumer(Stream capsuleStream, WebTransportSession session)
    {
        _capsuleStream = capsuleStream;
        _session = session;
    }

    private async ValueTask<long> ReadVariableLengthIntegerAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead;
        long value;
        while (!VariableLengthIntegerHelper.TryRead(_buffer.ActiveSpan, out value, out bytesRead))
        {
            _buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength);
            bytesRead = await _capsuleStream.ReadAsync(_buffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new WebTransportSessionClosedException("Session has been closed by peer", 0, "");
            }
            _buffer.Commit(bytesRead);
        }

        _buffer.Discard(bytesRead);

        return value;
    }
    /// <summary>Processes the next capsule in the provided capsule stream.
    /// If an unknown capsule type is received, the capsule is dropped and the call ends.</summary>
    /// <exception cref="ObjectDisposedException">When the capsule stream or the session have been disposed.</exception>
    public async Task ProcessNextCapsule(CancellationToken cancellationToken = default)
    {

        long capsuleType = await ReadVariableLengthIntegerAsync(cancellationToken).ConfigureAwait(false);
        long capsuleLength = await ReadVariableLengthIntegerAsync(cancellationToken).ConfigureAwait(false);
        int capsuleLengthInt;
        try
        {
            capsuleLengthInt = checked((int)capsuleLength);
        }
        catch (OverflowException)
        {
            throw new WebTransportException("Unknown capsule received"); // All known capsule lengths are less than int.MaxValue
        }

        _buffer.EnsureAvailableSpace(capsuleLengthInt);
        await _capsuleStream.ReadExactlyAsync(_buffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

        Capsule? capsule = capsuleType switch
        {
            CloseSessionCapsule.CapsuleCode => CloseSessionCapsule.Deserialize(_buffer.ActiveMemory),
            DrainSessionCapsule.CapsuleCode => DrainSessionCapsule.Deserialize(_buffer.ActiveMemory),
            MaxBidirectionalStreamsCapsule.CapsuleCode => MaxBidirectionalStreamsCapsule.Deserialize(_buffer.ActiveMemory),
            MaxUnidirectionalStreamsCapsule.CapsuleCode => MaxUnidirectionalStreamsCapsule.Deserialize(_buffer.ActiveMemory),
            MaxDataCapsule.CapsuleCode => MaxDataCapsule.Deserialize(_buffer.ActiveMemory),
            _ => null,
        };
        // Unknown capsules are silently dropped https://datatracker.ietf.org/doc/html/rfc9297#section-3.2-7

        _buffer.ClearAndReturnBuffer();

        capsule?.ProcessReceived(_session);
    }
}
