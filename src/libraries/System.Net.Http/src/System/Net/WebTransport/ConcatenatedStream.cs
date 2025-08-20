// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace System.Net.WebTransport;


internal sealed class ConcatenatedStream : Stream
{
    private Memory<byte> _memory;
    private readonly Stream _stream;
    private bool _isMemoryRead => _memoryPosition == _memory.Length;
    private int _memoryPosition;

    private bool _isDisposed;

    /// <exception cref="ArgumentNullException">When <paramref name="stream"/> is null</exception>
    public ConcatenatedStream(ArrayBuffer buffer, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _memory = buffer.ActiveMemory;
        _stream = stream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _memory.Length + _stream.Length;
        }
    }
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _memoryPosition + _stream.Position;
        }
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length) throw new ArgumentException($"The sum of {nameof(count)} and {nameof(offset)} is larger then the length of {nameof(buffer)}");

        int bytesToReadFromMemory = 0;
        if (!_isMemoryRead)
        {
            bytesToReadFromMemory = Math.Min(count, _memory.Length - _memoryPosition);
            _memory.Slice(_memoryPosition, bytesToReadFromMemory).CopyTo(buffer.AsMemory().Slice(offset));
            _memoryPosition += bytesToReadFromMemory;
        }

        int bytesToReadFromStream = count - bytesToReadFromMemory;
        int bytesReadFromStream = 0;
        if (bytesToReadFromStream > 0) // necessary because of https://github.com/dotnet/runtime/issues/118888
        {
            bytesReadFromStream = _stream.Read(buffer, offset + bytesToReadFromMemory, bytesToReadFromStream);
        }

        return bytesToReadFromMemory + bytesReadFromStream;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {

        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                _stream.Dispose();
                _memory = null;
            }
        }

        // Call base class implementation.
        base.Dispose(disposing);
    }
}
