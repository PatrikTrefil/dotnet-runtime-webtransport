// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Threading;
using System.Buffers.Binary;
using System.Diagnostics;

namespace System.Net.WebTransport;

// The capsule protocol can be found here https://datatracker.ietf.org/doc/html/rfc9297

internal sealed class CloseSessionCapsule
{
    public const long CapsuleCode = 0x2843;
    private static readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const int ApplicationErrorMessageLengthInBytesLimit = 8192;
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
    public void Process(WebTransportSession session)
    {
        string applicationErrorMessageString = _encoding.GetString(ApplicationErrorMessage.Span);
        session.ReceiveClose(ApplicationErrorCode, applicationErrorMessageString);
    }
    public static async Task<CloseSessionCapsule> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        long length = VariableLengthIntegerStreamHelper.Read(stream);
        long applicationErrorMessageLength = length - 4;
        if (applicationErrorMessageLength > ApplicationErrorMessageLengthInBytesLimit)
        {
            throw new WebTransportException("Application error message length exceeded");
        }
        Debug.Assert(ApplicationErrorMessageLengthInBytesLimit + 4 < sizeof(int));
        int intLength = (int)length;
        byte[] buffer = new byte[intLength];
        await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        bool isSuccess = BinaryPrimitives.TryReadUInt32BigEndian(buffer, out uint applicationErrorCode);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        ReadOnlyMemory<byte> applicationErrorMessage = new ArraySegment<byte>(buffer, 4, buffer.Length - 4);
        return new CloseSessionCapsule(applicationErrorCode, applicationErrorMessage);
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

internal sealed class DrainSessionCapsule
{
    public const long CapsuleCode = 0x78ae;
    public DrainSessionCapsule() { }
    [Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Keep the same interface for all capsule classes")]
    public async Task ProcessAsync(WebTransportSession session, CancellationToken cancellationToken = default)
    {
        await session.CloseAsync(cancellationToken).ConfigureAwait(false);
    }
    public static DrainSessionCapsule Deserialize(Stream stream) {
        long length = VariableLengthIntegerStreamHelper.Read(stream);
        if (length != 0)
        {
            throw new WebTransportException($"Invalid capsule length {length} bytes");
        }
        return new DrainSessionCapsule();
    }
    [Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Keep the same interface for all capsule classes")]
    public void Serialize(Stream stream) {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        VariableLengthIntegerStreamHelper.Write(stream, 0);
    }
}

internal sealed class MaxBidirectionalStreamsCapsule
{
    public const long CapsuleCode = 0x190B4D3F;
    public long MaxBidirectionalStreams { get; }
    public MaxBidirectionalStreamsCapsule(long maxBidirectionalStreams)
    {
        MaxBidirectionalStreams = maxBidirectionalStreams;
    }
    public void Process(WebTransportSession session)
    {
        session.BidirectionalStreamCountLimitForPeer = MaxBidirectionalStreams;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxBidirectionalStreamsCapsule Deserialize(Stream stream) {
        long length = VariableLengthIntegerStreamHelper.Read(stream);
        long maxBidirectionalStream = VariableLengthIntegerStreamHelper.Read(stream, out int maxBidirectionalStreamsBytesRead);
        if (length != maxBidirectionalStreamsBytesRead)
        {
            throw new WebTransportException("Deserialization failed because of invalid capsule data - received length does not match the payload length")
        }
        return new MaxBidirectionalStreamsCapsule(maxBidirectionalStream);
    }
    public void Serialize(Stream stream) {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = System.Net.Http.VariableLengthIntegerHelper.TryWrite(buffer, MaxBidirectionalStreams, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

internal sealed class MaxUnidirectionalStreamsCapsule
{
    public const long CapsuleCode = 0x190B4D40;
    public long MaxUnidirectionalStreams { get; }
    public MaxUnidirectionalStreamsCapsule(long maxUnidirectionalStreams)
    {
        MaxUnidirectionalStreams = maxUnidirectionalStreams;
    }
    public void Process(WebTransportSession session)
    {
        session.UnidirectionalStreamCountLimitForPeer = MaxUnidirectionalStreams;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxUnidirectionalStreamsCapsule Deserialize(Stream stream) {
        long length = VariableLengthIntegerStreamHelper.Read(stream);
        long maxUnidirectionalStream = VariableLengthIntegerStreamHelper.Read(stream, out int maxUnidirectionalStreamsBytesRead);
        if (length != maxUnidirectionalStreamsBytesRead)
        {
            throw new WebTransportException("Deserialization failed because of invalid capsule data - received length does not match the payload length")
        }
        return new MaxUnidirectionalStreamsCapsule(maxUnidirectionalStream);
    }
    public void Serialize(Stream stream) {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = System.Net.Http.VariableLengthIntegerHelper.TryWrite(buffer, MaxUnidirectionalStreams, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

internal sealed class MaxDataCapsule
{
    public const long CapsuleCode = 0x190B4D3D;
    public long MaxData { get; }
    public MaxDataCapsule(long maxData)
    {
        MaxData = maxData;
    }
    public void Process(WebTransportSession session)
    {
        session.MaxDataSentLimitForPeer = MaxData;
    }
    /// <exception cref="WebTransportException">When the received length does not match the payload length</exception>
    public static MaxDataCapsule Deserialize(Stream stream) {
        long length = VariableLengthIntegerStreamHelper.Read(stream);
        long maxData = VariableLengthIntegerStreamHelper.Read(stream, out int maxDataBytesRead);
        if (length != maxDataBytesRead)
        {
            throw new WebTransportException("Deserialization failed because of invalid capsule data - received length does not match the payload length")
        }
        return new MaxDataCapsule(maxData);
    }
    public void Serialize(Stream stream) {
        VariableLengthIntegerStreamHelper.Write(stream, CapsuleCode);
        Span<byte> buffer = stackalloc byte[VariableLengthIntegerStreamHelper.MaximumEncodedLength];
        bool isSuccess = System.Net.Http.VariableLengthIntegerHelper.TryWrite(buffer, MaxData, out int bytesWritten);
        Debug.Assert(isSuccess, $"{nameof(buffer)} should be large enough");
        for (int i = 0; i < bytesWritten; i++)
        {
            stream.WriteByte(buffer[i]);
        }
    }
}

internal sealed class CapsuleConsumer
{
    private readonly Stream _capsuleStream;
    private readonly WebTransportSession _session;
    public CapsuleConsumer(Stream capsuleStream, WebTransportSession session)
    {
        _capsuleStream = capsuleStream;
        _session = session;
    }
    /// <summary>Processes the next capsule in the provided capsule stream.
    /// If an unknown capsule type is received, the capsule is dropped and the call ends.</summary>
    /// <exception cref="ObjectDisposedException">When the capsule stream or the session have been disposed.</exception>
    public async Task ProcessNextCapsule(CancellationToken cancellationToken = default)
    {
        long incomingType = VariableLengthIntegerStreamHelper.Read(_capsuleStream);
        switch (incomingType)
        {
            case CloseSessionCapsule.CapsuleCode:
                CloseSessionCapsule closeSessionCapsule = await CloseSessionCapsule.DeserializeAsync(_capsuleStream, cancellationToken).ConfigureAwait(false);
                closeSessionCapsule.Process(_session);
                break;
            case DrainSessionCapsule.CapsuleCode:
                DrainSessionCapsule drainSessionCapsule = DrainSessionCapsule.Deserialize(_capsuleStream);
                await drainSessionCapsule.ProcessAsync(_session, cancellationToken).ConfigureAwait(false);
                break;
            case MaxBidirectionalStreamsCapsule.CapsuleCode:
                MaxBidirectionalStreamsCapsule maxBidirectionalStreamsCapsule = MaxBidirectionalStreamsCapsule.Deserialize(_capsuleStream);
                maxBidirectionalStreamsCapsule.Process(_session);
                break;
            case MaxUnidirectionalStreamsCapsule.CapsuleCode:
                MaxUnidirectionalStreamsCapsule maxUnidirectionalStreamsCapsule = MaxUnidirectionalStreamsCapsule.Deserialize(_capsuleStream);
                maxUnidirectionalStreamsCapsule.Process(_session);
                break;
            case MaxDataCapsule.CapsuleCode:
                MaxDataCapsule maxDataCapsule = MaxDataCapsule.Deserialize(_capsuleStream);
                maxDataCapsule.Process(_session);
                break;
        }
        // Unknown capsules are silently dropped https://datatracker.ietf.org/doc/html/rfc9297#section-3.2-7
    }
}
