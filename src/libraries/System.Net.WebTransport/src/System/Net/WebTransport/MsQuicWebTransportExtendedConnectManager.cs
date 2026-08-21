// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Net.Quic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace System.Net.WebTransport;

internal sealed class MsQuicWebTransportExtendedConnectManager : Http3ExtendedConnectManager, IMsQuicWebTransportHttpConnectionManager
{
    private Lock SyncObjSettingsValidation { get; } = new();
    private bool _isSettingsValidationDone;
    private Exception? _validationException;
    internal MsQuicWebTransportSessionManager SessionManager { get; }

    public MsQuicWebTransportExtendedConnectManager(Http3ExtendedConnectManagerCreationOptions options) : base(options)
    {
        SessionManager = new(this);
    }

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#name-stream-type-registration"/>
    public override long UnidirectionalStreamType => 0x54;

    /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-4.2-4"/>
    public override long BidirectionalStreamSignalValue => 0x41;

    public override void ValidateAndProcessServerSettings(Dictionary<long, long> serverSettings)
    {
        if (NetEventSource.Log.IsEnabled()) NetEventSource.Trace(this);

        Debug.Assert(serverSettings != null);

        lock (SyncObjSettingsValidation)
        {
            if (_isSettingsValidationDone)
            {
                if (_validationException != null)
                {
                    if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, _validationException);

                    throw _validationException;
                }

                return;
            }

            try
            {
                ValidateAndProcessServerSettingsCore(serverSettings);
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.TraceException(this, e);

                _validationException = e;
                throw;
            }
            finally
            {
                _isSettingsValidationDone = true;
            }
        }
    }

    private void ValidateAndProcessServerSettingsCore(Dictionary<long, long> serverSettings)
    {
        SessionManager.MaxSessionsCount = GetAndValidateSettingValue(serverSettings, Http3SettingType.WebTransportMaxSessions, 0);

        ThrowHelper.ValidateSessionCountLimit(SessionManager.MaxSessionsCount);

        SessionManager.InitialMaxUnidirectionalStreamsPerSession = GetAndValidateSettingValue(serverSettings, Http3SettingType.WebTransportInitialMaxUnidirectionalStreamsPerSession, 0);
        SessionManager.InitialMaxBidirectionalStreamsPerSession = GetAndValidateSettingValue(serverSettings, Http3SettingType.WebTransportInitialMaxBidirectionalStreamsPerSession, 0);
        SessionManager.InitialMaxDataPerSession = GetAndValidateSettingValue(serverSettings, Http3SettingType.WebTransportInitialMaxDataPerSession, 0);

        ThrowHelper.ValidateStreamCountLimit(SessionManager.InitialMaxUnidirectionalStreamsPerSession);
        ThrowHelper.ValidateStreamCountLimit(SessionManager.InitialMaxBidirectionalStreamsPerSession);
    }

    private static long GetAndValidateSettingValue(Dictionary<long, long> serverSettings, Http3SettingType settingType, long defaultValue)
    {
        return serverSettings.TryGetValue((long)settingType, out long settingValue) ? settingValue : defaultValue;
    }

    public override Task ProcessGoAwayAsync() => SessionManager.ProcessGoAwayAsync();

    public override Task ProcessReceivedStreamAsync(QuicStreamType streamType, byte[] initialData, QuicStream stream) => SessionManager.ProcessReceivedStreamAsync(streamType, initialData, stream);

    public override Task ReleaseSessionAfterFailedHandshakeAsync(QuicStream? quicStream) => SessionManager.ReleaseSessionAfterFailedHandshakeAsync(quicStream);

    public override void ReserveSession() => SessionManager.ReserveSession();

    void IMsQuicWebTransportHttpConnectionManager.RemoveOutboundStream(QuicStreamType type) => RemoveOutboundStream(type);

    Task IMsQuicWebTransportHttpConnectionManager.RemoveSessionAsync(QuicStream connectStream) => RemoveSessionAsync(connectStream);

    Task<QuicStream> IMsQuicWebTransportHttpConnectionManager.OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken) => OpenOutboundStreamAsync(type, cancellationToken);
}
