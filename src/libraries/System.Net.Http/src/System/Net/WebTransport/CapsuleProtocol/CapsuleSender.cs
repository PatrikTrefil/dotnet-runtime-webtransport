// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Buffers;
using System.Net.Quic;

namespace System.Net.WebTransport;

internal sealed class CapsuleSender
{
    private readonly QuicStream _capsuleStream;

    public CapsuleSender(QuicStream capsuleStream)
    {
        ArgumentNullException.ThrowIfNull(capsuleStream);

        _capsuleStream = capsuleStream;
    }
    /// <summary>
    /// Sends the capsule over the stream provided in the constructor and flushes the stream.
    /// </summary>
    /// <param name="capsule">Capsule to send.</param>
    /// <param name="completeWrites">If true, the FIN flag is sent.</param>
    /// <param name="cancellationToken"></param>
    public async Task SendCapsuleAsync(Capsule capsule, bool completeWrites, CancellationToken cancellationToken = default)
    {
        // TODO: maybe the cancellation should destroy the session? it should not be useable afterwards?
        ArgumentNullException.ThrowIfNull(capsule);

        byte[] arrayPoolBuffer = ArrayPool<byte>.Shared.Rent(capsule.TotalLength); // TODO: does this make sense for small capsules?

        capsule.Serialize(arrayPoolBuffer.AsSpan(0, capsule.TotalLength));
        await _capsuleStream.WriteAsync(arrayPoolBuffer.AsMemory(0, capsule.TotalLength), completeWrites, cancellationToken).ConfigureAwait(false);
        await _capsuleStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        ArrayPool<byte>.Shared.Return(arrayPoolBuffer);
    }

}
