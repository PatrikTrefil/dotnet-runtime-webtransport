// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport;

/// <summary>
/// Reads and processes capsules from a capsule stream.
/// </summary>
internal sealed class CapsuleConsumer : IDisposable
{
    private bool _isDisposed;
    private readonly Stream _capsuleStream;
    private readonly WebTransportSession _session;
    private readonly ArrayBuffer _buffer = new(initialSize: VariableLengthIntegerHelper.MaximumEncodedLength, usePool: true);
    public CapsuleConsumer(Stream capsuleStream, WebTransportSession session)
    {
        ArgumentNullException.ThrowIfNull(capsuleStream);
        ArgumentNullException.ThrowIfNull(session);

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
    /// <exception cref="ObjectDisposedException">When calling method on a disposed object.</exception>
    public async Task ProcessNextCapsule(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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

    public void Dispose()
    {
        _isDisposed = true;
        _buffer.Dispose();
    }
}
