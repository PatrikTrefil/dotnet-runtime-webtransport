// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.IO;
using System.Threading.Tasks;

namespace System.Net.WebTransport;

/// <summary>
/// Reads and processes capsules from a capsule stream.
/// </summary>
/// <remarks>Resources owned by this class are automatically disposed when an exception is thrown during <see cref="ProcessNextCapsule"/>.</remarks>
internal sealed class CapsuleConsumer
{
    private readonly Stream _capsuleStream;
    private readonly MsQuicWebTransportSession _session;
    // Don't make the _buffer readonly - mutable struct
    private ArrayBuffer _buffer;
    private const int s_maxCapsuleSize = 10_000; // Maximum possible capsule size of known capsule types in bytes

    /// <param name="capsuleStream">Stream with capsule data. The ownership of <paramref name="capsuleStream"/> is not passed to the created instance of <see cref="CapsuleConsumer"/>.</param>
    /// <param name="capsuleStreamBuffer">
    /// Buffer with pre-read data from the <paramref name="capsuleStream"/>. The ownership of this buffer is passed to the created instance of <see cref="CapsuleConsumer"/>.
    /// The created instance is therefore responsible for the disposal of the <paramref name="capsuleStreamBuffer"/>.
    /// </param>
    /// <param name="session">Session that owns this instance.</param>
    public CapsuleConsumer(Stream capsuleStream, ArrayBuffer capsuleStreamBuffer, MsQuicWebTransportSession session)
    {
        _capsuleStream = capsuleStream;
        _session = session;
        _buffer = capsuleStreamBuffer;
    }

    /// <exception cref="EndOfStreamException">When the end of stream has been reached. This can only happen if the stream was closed gracefully.</exception>
    private async Task<long> ReadVariableLengthInteger()
    {
        int bytesParsed;
        long capsuleCode;
        while (!VariableLengthIntegerHelper.TryRead(_buffer.ActiveSpan, out capsuleCode, out bytesParsed))
        {
            _buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength);
            int bytesRead = await _capsuleStream.ReadAsync(_buffer.AvailableMemory).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            _buffer.Commit(bytesRead);
        }

        _buffer.Discard(bytesParsed);
        return capsuleCode;
    }

    /// <summary>
    /// Processes the next capsule in the provided capsule stream.
    /// If a capsule with unknown capsule code is received, the capsule is dropped and the call ends.
    /// </summary>
    /// <exception cref="EndOfStreamException">When the capsule stream is cleanly terminated.</exception>
    /// <exception cref="CapsuleProtocolException">If the next capsule value is invalid.</exception>
    public async Task ProcessNextCapsule()
    {
        try
        {
            Capsule? capsule = await DeserializeCapsule().ConfigureAwait(false);

            if (capsule != null)
            {
                capsule.ProcessReceived(_session);
                if (NetEventSource.Log.IsEnabled()) NetEventSource.CapsuleDeserializationAndProccessingCompleted(this, "Capsule deserialization and processing finished.");
            }
            else
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.CapsuleDeserializationAndProccessingCompleted(this, "Unknown capsule dropped");
            }
        }
        catch (Exception)
        {
            _buffer.Dispose();
            throw;
        }
    }

    private async Task<Capsule?> DeserializeCapsule()
    {
        long capsuleCode = await ReadVariableLengthInteger().ConfigureAwait(false);

        long capsuleLength = await ReadVariableLengthInteger().ConfigureAwait(false);

        if (NetEventSource.Log.IsEnabled()) NetEventSource.CapsuleDeserializationAndProcessingStarted(this, $"Starting deserialization and processing of received capsule with code 0x{capsuleCode:X} with length {capsuleLength}.");

        if (capsuleLength > s_maxCapsuleSize)
        {
            await SkipExactlyAsync(capsuleLength).ConfigureAwait(false); // No capsule of this size are known, so we silently drop it https://datatracker.ietf.org/doc/html/rfc9297#section-3.2-7
            return null;
        }
        int capsuleLengthInt = (int)capsuleLength;

        if (capsuleLengthInt > _buffer.ActiveLength)
        {
            int readAtLeastBytes = capsuleLengthInt - _buffer.ActiveLength;
            _buffer.EnsureAvailableSpace(readAtLeastBytes);
            int bytesReadCapsuleValue = await _capsuleStream.ReadAtLeastAsync(_buffer.AvailableMemory, readAtLeastBytes).ConfigureAwait(false);
            _buffer.Commit(bytesReadCapsuleValue);
        }

        Capsule? capsule = DeserializeCapsuleValue(capsuleCode, _buffer.ActiveSpan.Slice(0, capsuleLengthInt));

        _buffer.Discard(capsuleLengthInt);

        return capsule;
    }

    /// <summary>
    /// Deserializes a capsule from the provided <paramref name="capsuleBuffer"/>.
    /// </summary>
    /// <param name="capsuleCode">Code of the capsule type</param>
    /// <param name="capsuleBuffer">Buffer that contains the data deserialize. There must be no extra data.</param>
    /// <returns>Deserialized capsule or null if the <paramref name="capsuleCode"/> is unknown.</returns>
    /// <exception cref="CapsuleProtocolException">If the capsule value is invalid.</exception>
    private static Capsule? DeserializeCapsuleValue(long capsuleCode, ReadOnlySpan<byte> capsuleBuffer)
    {
        return capsuleCode switch
        {
            CloseSessionCapsule.s_code => CloseSessionCapsule.Deserialize(capsuleBuffer),
            DrainSessionCapsule.s_code => DrainSessionCapsule.Deserialize(capsuleBuffer),
            MaxBidirectionalStreamsCapsule.s_code => MaxBidirectionalStreamsCapsule.Deserialize(capsuleBuffer),
            MaxUnidirectionalStreamsCapsule.s_code => MaxUnidirectionalStreamsCapsule.Deserialize(capsuleBuffer),
            MaxDataCapsule.s_code => MaxDataCapsule.Deserialize(capsuleBuffer),
            _ => null, // Unknown capsules are silently dropped https://datatracker.ietf.org/doc/html/rfc9297#section-3.2-7
        };
    }

    public async Task SkipExactlyAsync(long bytesToSkip)
    {
        int bytesToDiscard = Math.Min(
            bytesToSkip <= int.MaxValue ? (int)bytesToSkip : int.MaxValue,
            _buffer.ActiveLength
            );

        _buffer.Discard(bytesToDiscard);
        bytesToSkip -= bytesToDiscard;

        if (bytesToSkip == 0)
        {
            return;
        }

        // At this point the _buffer is empty

        int bytesToReadInOneIteration = 2048;
        _buffer.EnsureAvailableSpace(bytesToReadInOneIteration);
        while (bytesToSkip >= bytesToReadInOneIteration)
        {
            await _capsuleStream.ReadExactlyAsync(_buffer.AvailableMemory).ConfigureAwait(false);
            bytesToSkip -= bytesToReadInOneIteration;
        }

        await _capsuleStream.ReadExactlyAsync(_buffer.AvailableMemory.Slice(0, (int)bytesToSkip)).ConfigureAwait(false);
    }
}
