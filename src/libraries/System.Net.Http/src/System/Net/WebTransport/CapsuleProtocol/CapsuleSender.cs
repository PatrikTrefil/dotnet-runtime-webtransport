// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Buffers;
using System.Net.Quic;

namespace System.Net.WebTransport;

internal sealed class CapsuleSender
{
    private readonly QuicStream _capsuleStream;
    private readonly SemaphoreSlim _semaphore = new(1);

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
        if (NetEventSource.Log.IsEnabled())
        {
            string logMessage = $"Sending capsule of type 0x{capsule.Code:X}";
            if (completeWrites) logMessage += " and closing the stream";

            NetEventSource.SendCapsuleAsyncStarted(this, logMessage);
        }

        ArgumentNullException.ThrowIfNull(capsule);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendCapsuleAsyncCore(capsule, completeWrites, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }

        if (NetEventSource.Log.IsEnabled()) NetEventSource.SendCapsuleAsyncCompleted(this, "Capsule sent");
    }

    private async Task SendCapsuleAsyncCore(Capsule capsule, bool completeWrites, CancellationToken cancellationToken = default)
    {
        byte[] arrayPoolBuffer = ArrayPool<byte>.Shared.Rent(capsule.TotalLength); // TODO: does this make sense for small capsules?

        capsule.Serialize(arrayPoolBuffer.AsSpan(0, capsule.TotalLength));
        await _capsuleStream.WriteAsync(arrayPoolBuffer.AsMemory(0, capsule.TotalLength), completeWrites, cancellationToken).ConfigureAwait(false);
        await _capsuleStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        ArrayPool<byte>.Shared.Return(arrayPoolBuffer);
    }
}
