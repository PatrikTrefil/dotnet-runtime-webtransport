// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.IO;
using System.Threading.Tasks;

namespace System.Net.WebTransport;

/// <summary>
/// Reads and processes capsules from a capsule stream.
/// </summary>
internal sealed class CapsuleConsumer : IDisposable
{
    private bool _isDisposed;
    private readonly Stream _capsuleStream;
    private readonly WebTransportSession _session;
    // Don't make the _buffer readonly - mutable struct
    private ArrayBuffer _buffer;
    private const int s_maxCapsuleSize = 10_000; // Maximum possible capsule size of known capsule types in bytes

    public CapsuleConsumer(Stream capsuleStream, byte[] capsuleStreamBuffer,  WebTransportSession session)
    {
        ArgumentNullException.ThrowIfNull(capsuleStream);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(capsuleStreamBuffer);

        _capsuleStream = capsuleStream;
        _session = session;
        _buffer = new(initialSize: capsuleStreamBuffer.Length, usePool: true);
        capsuleStreamBuffer.CopyTo(_buffer.AvailableSpan);
        _buffer.Commit(capsuleStreamBuffer.Length);
    }

    /// <exception cref="EndOfStreamException">When the end of stream has been reached. This can only happen if the stream was closed gracefully.</exception>
    private async Task<long> ReadVariableLengthInteger()
    {
        int bytesParsed;
        long capsuleType;
        while (!VariableLengthIntegerHelper.TryRead(_buffer.ActiveSpan, out capsuleType, out bytesParsed))
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
        return capsuleType;
    }

    /// <summary>
    /// Processes the next capsule in the provided capsule stream.
    /// If an unknown capsule type is received, the capsule is dropped and the call ends.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When calling method on a disposed object.</exception>
    /// <exception cref="EndOfStreamException">When the capsule stream is cleanly terminated.</exception>
    public async Task ProcessNextCapsule()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Capsule? capsule = await DeserializeCapsule().ConfigureAwait(false);

        capsule?.ProcessReceived(_session);
    }

    private async Task<Capsule?> DeserializeCapsule()
    {
        long capsuleType = await ReadVariableLengthInteger().ConfigureAwait(false);

        long capsuleLength = await ReadVariableLengthInteger().ConfigureAwait(false);

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

        Capsule? capsule = DeserializeCapsuleValue(capsuleType, _buffer.ActiveMemory.Slice(0, capsuleLengthInt));

        _buffer.Discard(capsuleLengthInt);

        return capsule;
    }

    /// <summary>
    /// Deserializes a capsule from the provided capsule type and buffer.
    /// </summary>
    /// <param name="capsuleType">Code of the capsule type</param>
    /// <param name="capsuleBuffer">Buffer that contains the data deserialize. There must be no extra data.</param>
    /// <returns></returns>
    private static Capsule? DeserializeCapsuleValue(long capsuleType, ReadOnlyMemory<byte> capsuleBuffer)
    {
        return capsuleType switch
        {
            CloseSessionCapsule.CapsuleCode => CloseSessionCapsule.Deserialize(capsuleBuffer),
            DrainSessionCapsule.CapsuleCode => DrainSessionCapsule.Deserialize(capsuleBuffer),
            MaxBidirectionalStreamsCapsule.CapsuleCode => MaxBidirectionalStreamsCapsule.Deserialize(capsuleBuffer),
            MaxUnidirectionalStreamsCapsule.CapsuleCode => MaxUnidirectionalStreamsCapsule.Deserialize(capsuleBuffer),
            MaxDataCapsule.CapsuleCode => MaxDataCapsule.Deserialize(capsuleBuffer),
            _ => null, // Unknown capsules are silently dropped https://datatracker.ietf.org/doc/html/rfc9297#section-3.2-7
        };
    }

    public async Task SkipExactlyAsync(long minimumBytes)
    {
        long totalBytesRead = 0;
        int bytesToReadInOneIteration = 2048;
        while (minimumBytes - totalBytesRead >= bytesToReadInOneIteration)
        {
            _buffer.EnsureAvailableSpace(bytesToReadInOneIteration);
            await _capsuleStream.ReadExactlyAsync(_buffer.AvailableMemory).ConfigureAwait(false);
            totalBytesRead += bytesToReadInOneIteration;
        }
        int remainingBytes = (int)(minimumBytes - totalBytesRead);
        await _capsuleStream.ReadExactlyAsync(_buffer.AvailableMemory.Slice(0, remainingBytes)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _isDisposed = true;
        _buffer.Dispose();
    }
}
