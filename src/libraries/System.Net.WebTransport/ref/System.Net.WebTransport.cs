// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Net.WebTransport
{
    public abstract partial class WebTransportStream : System.IO.Stream
    {
        protected internal WebTransportStream(WebTransportStreamType type) { }
        public abstract long StreamId { get; }
        public WebTransportStreamType Type { get { throw null; } }
        public abstract System.Threading.Tasks.Task ReadsClosed { get; }
        public abstract System.Threading.Tasks.Task WritesClosed { get; }
        public override long Length { get { throw null; } }
        public override long Position { get { throw null; } set { } }
        public abstract void CompleteWrites();
        public override System.Threading.Tasks.ValueTask WriteAsync(System.ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) { throw null; }
        public abstract System.Threading.Tasks.ValueTask WriteAsync(System.ReadOnlyMemory<byte> buffer, bool completeWrites, System.Threading.CancellationToken cancellationToken = default);
        public abstract override System.IAsyncResult BeginWrite(byte[] buffer, int offset, int count, System.AsyncCallback? callback, object? state);
        public abstract override void WriteByte(byte value);
        public abstract override void Write(System.ReadOnlySpan<byte> buffer);
        public abstract override void Write(byte[] buffer, int offset, int count);
        public abstract void Abort(WebTransportAbortDirection abortDirection, long errorCode);
        public override long Seek(long offset, System.IO.SeekOrigin origin) { throw null; }
        public override void SetLength(long value) { }
        protected override void Dispose(bool disposing) { }
        public sealed override System.Threading.Tasks.ValueTask DisposeAsync() { throw null; }
        protected virtual System.Threading.Tasks.ValueTask DisposeAsyncCore() { throw null; }
    }
    public enum WebTransportSessionState
    {
        None = 0,
        Open = 1,
        ClosedRemotely = 2,
        ClosedLocally = 3,
        AbortedLocally = 4,
        AbortedRemotely = 5
    }
    public enum WebTransportStreamType
    {
        Unidirectional = 0,
        Bidirectional = 1
    }
    public sealed partial class WebTransportSessionCreationOptions
    {
        public required System.Uri Uri { get { throw null; } init { } }
        public required System.Net.Http.HttpMessageInvoker HttpMessageInvoker { get { throw null; } init { } }
        public System.Func<WebTransportSession, System.Threading.Tasks.Task> GracefulShutdownHandler { get { throw null; } init { } }
        public string[]? AvailableSubProtocols { get { throw null; } init { } }
        public long InitialUnidirectionalStreamCountLimitForPeer { get { throw null; } init { } }
        public long InitialBidirectionalStreamCountLimitForPeer { get { throw null; } init { } }
        public long InitialDataSentLimitForPeer { get { throw null; } init { } }
        public required long DefaultStreamErrorCode { get { throw null; } init { } }
        public required System.Version HttpVersion { get { throw null; } init { } }
        public System.Net.Http.HttpVersionPolicy HttpVersionPolicy { get { throw null; } init { } }
    }
    public static class ClientWebTransportSession
    {
        public static System.Threading.Tasks.Task<WebTransportSession> ConnectAsync(WebTransportSessionCreationOptions options, System.Threading.CancellationToken cancellationToken = default) { throw null; }
    }
    public abstract partial class WebTransportSession : System.IAsyncDisposable
    {
        internal WebTransportSession() { }
        protected System.Func<System.Threading.Tasks.Task> GracefulShutdownHandler { get { throw null; } }
        protected long DefaultStreamErrorCode { get { throw null; } }
        public long Id { get { throw null; } }
        public string? SubProtocol { get { throw null; } }
        public abstract WebTransportSessionState State { get; protected set; }
        public abstract long UnidirectionalStreamCountLimitProvidedByPeer { get; internal set; }
        public abstract long UnidirectionalStreamCountLimitForPeer { get; protected set; }
        public abstract System.Threading.Tasks.ValueTask SetUnidirectionalStreamCountLimitForPeerAsync(long limit, System.Threading.CancellationToken cancellationToken = default);
        public abstract long BidirectionalStreamCountLimitProvidedByPeer { get; internal set; }
        public abstract long BidirectionalStreamCountLimitForPeer { get; protected set; }
        public abstract System.Threading.Tasks.ValueTask SetBidirectionalStreamCountLimitForPeerAsync(long limit, System.Threading.CancellationToken cancellationToken = default);
        public abstract long DataSentLimitProvidedByPeer { get; internal set; }
        public abstract long DataSentLimitForPeer { get; protected set; }
        public abstract System.Threading.Tasks.ValueTask SetDataSentLimitForPeerAsync(long limit, System.Threading.CancellationToken cancellationToken = default);
        public abstract long? CloseStatusCode { get; protected set; }
        public abstract string? CloseStatusDescription { get; protected set; }
        protected WebTransportException? GetExceptionForObjectState() { throw null; }
        protected void ThrowIfInvalidState() { }
        public abstract System.Threading.Tasks.ValueTask RequestCloseAsync(System.Threading.CancellationToken cancellationToken = default);
        public System.Threading.Tasks.ValueTask CloseAsync() { throw null; }
        public System.Threading.Tasks.ValueTask CloseAsync(long closeStatus, string statusDescription, System.Threading.CancellationToken cancellationToken = default) { throw null; }
        protected abstract System.Threading.Tasks.ValueTask CloseAsyncCore(long closeStatus, byte[] statusDescription, System.Threading.CancellationToken cancellationToken = default);
        public System.Threading.Tasks.ValueTask<WebTransportStream> OpenOutboundStreamAsync(WebTransportStreamType type, System.Threading.CancellationToken cancellationToken = default) { throw null; }
        protected abstract System.Threading.Tasks.ValueTask<WebTransportStream> OpenOutboundStreamAsyncCore(WebTransportStreamType type, System.Threading.CancellationToken cancellationToken = default);
        public System.Threading.Tasks.ValueTask<WebTransportStream> AcceptInboundStreamAsync(WebTransportStreamType type, System.Threading.CancellationToken cancellationToken = default) { throw null; }
        protected abstract System.Threading.Tasks.ValueTask<WebTransportStream> AcceptInboundStreamAsyncCore(WebTransportStreamType type, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.ValueTask System.IAsyncDisposable.DisposeAsync() { throw null; }
        protected virtual System.Threading.Tasks.ValueTask DisposeAsyncCore() { throw null; }
    }
    public enum WebTransportError
    {
        Success = 0,
        InternalError = 1,
        SessionClosedByPeer = 2,
        StreamAborted = 3,
        TransportLayerError = 4,
        SessionConnectFailure = 5,
        OperationAborted = 6,
        CallbackError = 7,
        UnsupportedProtocol = 8,
        HeaderError = 9,
        RedirectRequired = 10,
        LimitExceeded = 11
    }
    public sealed partial class WebTransportException : System.Exception
    {
        public WebTransportException(WebTransportError error, string message) { }
        public WebTransportException(WebTransportError error, string message, System.Exception? innerException) { }
        public WebTransportException(WebTransportError error, string message, System.Uri? redirectUri) { }
        public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message) { }
        public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, string message, System.Exception? innerException) { }
        public WebTransportException(WebTransportError error, long? applicationErrorCode, string? applicationErrorMessage, System.Uri? redirectUri, string message, System.Exception? innerException) { }
        public WebTransportError WebTransportError { get { throw null; } }
        public long? CloseStatusCode { get { throw null; } }
        public string? CloseStatusDescription { get { throw null; } }
        public System.Uri? RedirectLocation { get { throw null; } }
    }
    public enum WebTransportAbortDirection
    {
        Read = 1,
        Write = 2,
        Both = 3
    }
}
