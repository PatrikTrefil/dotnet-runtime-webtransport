// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !TARGET_BROWSER && !TARGET_WASI && !TARGETS_BROWSER
using System.Collections.Generic;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Tests;

public sealed class TestHttp3ExtendedConnectManager : Http3ExtendedConnectManager
{
    // Keep test values in extension/private-use ranges and in disjoint buckets per stream kind.
    // The manager id offset makes values unique across managers used in the same process.
    private const long TestUnidirectionalStreamTypeBase = 0x100;
    private const long TestBidirectionalStreamSignalBase = 0x200;

    private readonly Func<Task> _processGoAwayAsync;
    private readonly Func<QuicStreamType, byte[], QuicStream, Task> _processReceivedStreamAsync;
    private readonly Action<Dictionary<long, long>> _validateAndProcessServerSettings;
    private readonly Action _reserveSession;
    private readonly Action<QuicStream?> _releaseSessionAfterFailedHandshake;

    public static long GetTestUnidirectionalStreamType(int managerId) => TestUnidirectionalStreamTypeBase + managerId;

    public static long GetTestBidirectionalStreamSignalValue(int managerId) => TestBidirectionalStreamSignalBase + managerId;

    public TestHttp3ExtendedConnectManager(
        Http3ExtendedConnectManagerCreationOptions options,
        Func<Task>? processGoAwayAsync = null,
        Func<QuicStreamType, byte[], QuicStream, Task>? processReceivedStreamAsync = null,
        long unidirectionalStreamType = 0,
        long bidirectionalStreamSignalValue = 0,
        Action<Dictionary<long, long>>? validateAndProcessServerSettings = null,
        Action? reserveSession = null,
        Action<QuicStream?>? releaseSessionAfterFailedHandshake = null)
        : base(options)
    {
        _processGoAwayAsync = processGoAwayAsync ?? (() => Task.CompletedTask);
        _processReceivedStreamAsync = processReceivedStreamAsync ?? ((streamType, initialData, stream) => Task.CompletedTask);
        UnidirectionalStreamType = unidirectionalStreamType;
        BidirectionalStreamSignalValue = bidirectionalStreamSignalValue;
        _validateAndProcessServerSettings = validateAndProcessServerSettings ?? (_ => { });
        _reserveSession = reserveSession ?? (() => { });
        _releaseSessionAfterFailedHandshake = releaseSessionAfterFailedHandshake ?? (_ => { });
    }

    public Task<QuicStream> OpenOutboundStreamForTestAsync(QuicStreamType streamType, CancellationToken cancellationToken)
        => OpenOutboundStreamAsync(streamType, cancellationToken);

    public Task RemoveSessionForTestAsync(QuicStream connectStream)
        => RemoveSessionAsync(connectStream);

    public void RemoveOutboundStreamForTest()
        => RemoveOutboundStream();

    public override Task ProcessGoAwayAsync() => _processGoAwayAsync();

    public override Task ProcessReceivedStreamAsync(QuicStreamType streamType, byte[] initialData, QuicStream stream)
        => _processReceivedStreamAsync(streamType, initialData, stream);

    public override long UnidirectionalStreamType { get; }

    public override long BidirectionalStreamSignalValue { get; }

    public override void ValidateAndProcessServerSettings(Dictionary<long, long> serverSettings)
        => _validateAndProcessServerSettings(serverSettings);

    public override void ReserveSession()
        => _reserveSession();

    public override void ReleaseSessionAfterFailedHandshake(QuicStream? quicStream)
        => _releaseSessionAfterFailedHandshake(quicStream);
}
#endif
