// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using System.Threading;
using System.Buffers;
using System.Net.Quic;

namespace System.Net.WebTransport;

/// <summary>
/// Sends capsules over a provided QUIC stream.
/// </summary>
/// <remarks>This class is not thread-safe.</remarks>
internal sealed class CapsuleSender
{
    private readonly QuicStream _capsuleStream;

    public CapsuleSender(QuicStream capsuleStream)
    {
        _capsuleStream = capsuleStream;
    }

    /// <summary>
    /// Sends the capsule over the stream provided in the constructor and flushes the stream.
    /// </summary>
    /// <param name="capsule">Capsule to send.</param>
    /// <param name="completeWrites">If true, the FIN flag is sent.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="QuicException">When a transport layer exception occurs.</exception>
    public async Task SendCapsuleAsync(Capsule capsule, bool completeWrites, CancellationToken cancellationToken = default)
    {
        if (NetEventSource.Log.IsEnabled())
        {
            NetEventSource.SendCapsuleAsyncStarted(this, $"Sending capsule of type 0x{capsule.Code:X} (completeWrites: {completeWrites})");
        }

        await SendCapsuleAsyncCore(capsule, completeWrites, cancellationToken).ConfigureAwait(false);

        if (NetEventSource.Log.IsEnabled()) NetEventSource.SendCapsuleAsyncCompleted(this);
    }

    private async Task SendCapsuleAsyncCore(Capsule capsule, bool completeWrites, CancellationToken cancellationToken = default)
    {
        byte[] arrayPoolBuffer = ArrayPool<byte>.Shared.Rent(capsule.TotalLength);
        try
        {
            capsule.Serialize(arrayPoolBuffer.AsSpan(0, capsule.TotalLength));
            await _capsuleStream.WriteAsync(arrayPoolBuffer.AsMemory(0, capsule.TotalLength), completeWrites, cancellationToken).ConfigureAwait(false);
            await _capsuleStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(arrayPoolBuffer);
        }
    }
}
