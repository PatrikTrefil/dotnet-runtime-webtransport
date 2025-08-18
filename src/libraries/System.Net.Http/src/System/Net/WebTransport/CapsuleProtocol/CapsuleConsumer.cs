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

        long capsuleType = await ReadVariableLengthInteger().ConfigureAwait(false);

        long capsuleLength = await ReadVariableLengthInteger().ConfigureAwait(false);

        int capsuleLengthInt;
        try
        {
            capsuleLengthInt = checked((int)capsuleLength);
        }
        catch (OverflowException)
        {
            throw new WebTransportException("Unknown capsule received"); // All known capsule lengths are less than int.MaxValue
        }

        if (capsuleLengthInt > _buffer.ActiveLength)
        {
            int readAtLeastBytes = capsuleLengthInt - _buffer.ActiveLength;
            _buffer.EnsureAvailableSpace(readAtLeastBytes);
            int bytesReadCapsuleValue = await _capsuleStream.ReadAtLeastAsync(_buffer.AvailableMemory, readAtLeastBytes).ConfigureAwait(false);
            _buffer.Commit(bytesReadCapsuleValue);
        }

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

        _buffer.Discard(capsuleLengthInt);

        capsule?.ProcessReceived(_session);
    }

    public void Dispose()
    {
        _isDisposed = true;
        _buffer.Dispose();
    }
}
