// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;

namespace System.Net.WebTransport;

// The capsule protocol can be found here https://datatracker.ietf.org/doc/html/rfc9297

internal abstract class Capsule
{
    public abstract long Code { get; }
    /// <summary>
    /// Length of the capsule when serialized in bytes.
    /// </summary>
    public int TotalLength => CapsuleCodeEncodedAsVariableLengthInteger.Length + VariableLengthIntegerHelper.GetByteCount(ValueLength) + ValueLength;
    protected abstract byte[] CapsuleCodeEncodedAsVariableLengthInteger { get; }
    /// <summary>
    /// Length of the Capsule Value.
    /// </summary>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc9297#name-capsule-format" />
    protected abstract int ValueLength { get; }
    /// <summary>
    /// Processes the capsule received from the peer.
    /// </summary>
    /// <param name="session">WebTransport session which received the capsule.</param>
    public abstract void ProcessReceived(MsQuicWebTransportSession session);

    /// <summary>
    ///  Serializes the capsule to the provided <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Buffer to which the capsule will be serialized.</param>
    /// <exception cref="ArgumentException">When the <paramref name="buffer"/> length is less than <see cref="TotalLength"/></exception>
    public abstract void Serialize(Span<byte> buffer);
}
