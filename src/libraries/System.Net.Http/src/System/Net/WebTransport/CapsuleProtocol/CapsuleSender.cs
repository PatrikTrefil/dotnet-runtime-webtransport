// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net.WebTransport;

internal sealed class CapsuleSender : IDisposable
{
    private bool _isDisposed;
    private readonly Stream _capsuleStream;
    private readonly ArrayBuffer _buffer = new(initialSize: VariableLengthIntegerHelper.MaximumEncodedLength, usePool: true);

    public CapsuleSender(Stream capsuleStream)
    {
        ArgumentNullException.ThrowIfNull(capsuleStream);

        _capsuleStream = capsuleStream;
    }
    /// <summary>
    /// Sends the capsule over the stream provided in the constructor and flushes the stream.
    /// </summary>
    /// <param name="capsule">Capsule to send.</param>
    /// <param name="cancellationToken"></param>
    public async Task SendCapsuleAsync(Capsule capsule, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(capsule);

        _buffer.EnsureAvailableSpace(capsule.TotalLength);
        capsule.Serialize(_buffer.AvailableSpan);
        await _capsuleStream.WriteAsync(_buffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
        await _capsuleStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _buffer.ClearAndReturnBuffer();
    }
    public void Dispose()
    {
        _isDisposed = true;
        _buffer.Dispose();
    }

}
