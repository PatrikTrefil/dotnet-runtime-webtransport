// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebTransport;


/// <summary>
/// Read-only stream that concatenates an <see cref="ArrayBuffer"/> of pre-read data and a <see cref="Stream"/>.
/// </summary>
internal sealed class ConcatenatedStream : Stream
{
    private Memory<byte> BufferMemory => _buffer.ActiveMemory;
    private readonly ArrayBuffer _buffer;
    private readonly Stream _stream;
    private bool _isMemoryRead;
    private int _memoryPosition;

    private bool _isDisposed;

    /// <exception cref="ArgumentNullException">When <paramref name="stream"/> is null</exception>
    public ConcatenatedStream(ArrayBuffer buffer, Stream stream)
    {
        _buffer = buffer;
        _stream = stream;
    }

    public override bool CanRead => !_isDisposed && _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _buffer.ActiveLength + _stream.Length;
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

    public override void Flush() => throw new InvalidOperationException();

    public override Task FlushAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    #region Reads

    #region Read call forwards

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
         => TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, default), callback, state);

    public override int EndRead(IAsyncResult asyncResult)
        => TaskToAsyncResult.End<int>(asyncResult);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int ReadByte()
    {
        byte b = 0;
        return Read(new Span<byte>(ref b)) != 0 ? b : -1;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    #endregion

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int bytesToReadFromMemory = 0;
        if (!_isMemoryRead)
        {
            bytesToReadFromMemory = Math.Min(buffer.Length, BufferMemory.Length - _memoryPosition);
            BufferMemory.Slice(_memoryPosition, bytesToReadFromMemory).CopyTo(buffer);
            _memoryPosition += bytesToReadFromMemory;

            if (_memoryPosition == BufferMemory.Length)
            {
                _isMemoryRead = true;
                _buffer.Dispose();
            }
        }

        int bytesToReadFromStream = buffer.Length - bytesToReadFromMemory;
        int bytesReadFromStream = 0;
        if (bytesToReadFromStream > 0) // necessary because of https://github.com/dotnet/runtime/issues/118888
        {
            bytesReadFromStream = await _stream.ReadAsync(buffer.Slice(bytesToReadFromMemory, bytesToReadFromStream), cancellationToken).ConfigureAwait(false);
        }

        return bytesToReadFromMemory + bytesReadFromStream;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int bytesToReadFromMemory = 0;
        if (!_isMemoryRead)
        {
            bytesToReadFromMemory = Math.Min(buffer.Length, BufferMemory.Length - _memoryPosition);
            BufferMemory.Slice(_memoryPosition, bytesToReadFromMemory).Span.CopyTo(buffer);
            _memoryPosition += bytesToReadFromMemory;

            if (_memoryPosition == BufferMemory.Length)
            {
                _isMemoryRead = true;
                _buffer.Dispose();
            }
        }

        int bytesToReadFromStream = buffer.Length - bytesToReadFromMemory;
        int bytesReadFromStream = 0;
        if (bytesToReadFromStream > 0) // necessary because of https://github.com/dotnet/runtime/issues/118888
        {
            bytesReadFromStream = _stream.Read(buffer.Slice(bytesToReadFromMemory, bytesToReadFromStream));
        }

        return bytesToReadFromMemory + bytesReadFromStream;
    }

    #endregion

    #region Writes

    // Writes throw InvalidOperationException because this is a read-only stream.

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw new InvalidOperationException();

    public override void EndWrite(IAsyncResult asyncResult) => throw new InvalidOperationException();

    public override void WriteByte(byte value) => throw new InvalidOperationException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

    #endregion

    protected override void Dispose(bool disposing)
    {

        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                _stream.Dispose();
                _buffer.Dispose();
            }
        }

        // Call base class implementation.
        base.Dispose(disposing);
    }
}
