// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;
using System.Net.Quic;

namespace System.Net.WebTransport.Functional.Tests;

internal static class WebTransportStreamTestHelper
{
    public static async Task AssertAllOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    public static async Task AssertWriteOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(() => stream.WriteAsync(new byte[1]).AsTask()),
            Assert.Throws<TException>(() => stream.WriteByte(2)),
            Assert.Throws<TException>(() => stream.Write(new byte[1])),
            Assert.Throws<TException>(() => {
                IAsyncResult result = stream.BeginWrite(new byte[1], 0, 1, null, null);
                result.AsyncWaitHandle.WaitOne();
                stream.EndWrite(result);
            })
        ];
        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    public static async Task AssertReadOperationsOnStreamThrowAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAsync<TException>(() => stream.ReadAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.ReadAtLeastAsync(new byte[1], 1).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.ReadExactlyAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAsync<TException>(() => stream.CopyToAsync(new MemoryStream(), 10)),
            Assert.Throws<TException>(() => stream.CopyTo(new MemoryStream(), 10)),
            Assert.Throws<TException>(() => stream.ReadByte()),
            Assert.Throws<TException>(() => stream.Read(new byte[1])),
            Assert.Throws<TException>(() => stream.ReadExactly(new byte[1])),
            Assert.Throws<TException>(() => {
                IAsyncResult result = stream.BeginRead(new byte[1], 0, 1, null, null);
                result.AsyncWaitHandle.WaitOne();
                _ = stream.EndRead(result);
            })
        ];
        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    public static async Task AssertReadOperationsOnStreamThrowAnyAsync<TException>(Stream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException[] exceptions = [
            await Assert.ThrowsAnyAsync<TException>(() => stream.ReadAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAnyAsync<TException>(() => stream.ReadAtLeastAsync(new byte[1], 1).AsTask()),
            await Assert.ThrowsAnyAsync<TException>(() => stream.ReadExactlyAsync(new byte[1]).AsTask()),
            await Assert.ThrowsAnyAsync<TException>(() => stream.CopyToAsync(new MemoryStream(), 10)),
            Assert.ThrowsAny<TException>(() => stream.CopyTo(new MemoryStream(), 10)),
            Assert.ThrowsAny<TException>(() => stream.ReadByte()),
            Assert.ThrowsAny<TException>(() => stream.Read(new byte[1])),
            Assert.ThrowsAny<TException>(() => stream.ReadExactly(new byte[1])),
            Assert.ThrowsAny<TException>(() => {
                IAsyncResult result = stream.BeginRead(new byte[1], 0, 1, null, null);
                result.AsyncWaitHandle.WaitOne();
                _ = stream.EndRead(result);
            })
        ];
        foreach (TException ex in exceptions)
        {
            exceptionValidator?.Invoke(ex);
        }
    }

    public static async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    public static async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(QuicStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    public static async Task AssertReadOperationsAndReadsClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.ReadsClosed);
        exceptionValidator?.Invoke(ex);
        await AssertReadOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }

    public static async Task AssertWriteOperationsAndWritesClosedOnStreamThrowAsync<TException>(WebTransportStream stream, Action<TException>? exceptionValidator) where TException : Exception
    {
        TException ex = await Assert.ThrowsAsync<TException>(() => stream.WritesClosed);
        exceptionValidator?.Invoke(ex);
        await AssertWriteOperationsOnStreamThrowAsync(stream, exceptionValidator);
    }
}
