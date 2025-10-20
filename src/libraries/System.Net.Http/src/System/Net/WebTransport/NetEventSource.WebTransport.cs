// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace System.Net;

// TODO: maybe some events should be visible to the user? (so not in Private.InternalDiagnostics)

// TODO: uncomment when WT is separated from System.Net.Http
// TODO: once separated add to TestEventListener
//[EventSource(Name = "Private.InternalDiagnostics.System.Net.WebTransport")]
internal sealed partial class NetEventSource
{
    private const int WtTraceId = HandlerErrorId + 1;
    private const int SessionMessageId = WtTraceId + 1;
    private const int StreamMessageId = SessionMessageId + 1;
    private const int CloseSessionStartId = StreamMessageId + 1;
    private const int CloseSessionStopId = CloseSessionStartId + 1;
    private const int OpenOutboundStreamStartId = CloseSessionStopId + 1;
    private const int OpenOutboundStreamStopId = OpenOutboundStreamStartId + 1;
    private const int AcceptInboundStreamStartId = OpenOutboundStreamStopId + 1;
    private const int AcceptInboundStreamStopId = AcceptInboundStreamStartId + 1;
    private const int CapsuleDeserializationAndProcessingStartId = AcceptInboundStreamStopId + 1;
    private const int CapsuleDeserializationAndProcessingStopId = CapsuleDeserializationAndProcessingStartId + 1;
    private const int SendCapsuleAsyncStartId = CapsuleDeserializationAndProcessingStopId + 1;
    private const int SendCapsuleAsyncStopId = SendCapsuleAsyncStartId + 1;

    #region Debug messages

    [Event(WtTraceId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void WtTrace(string objName, string memberName, string message) =>
            WriteEvent(WtTraceId, objName, memberName, message);

    [NonEvent]
    public static void Trace(object? obj, string? message = null, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.WtTrace(IdOf(obj), memberName ?? MissingMember, message ?? memberName ?? string.Empty);
    }

    [NonEvent]
    public static void TraceException(object? obj, Exception exception, [CallerMemberName] string? memberName = null)
        => Trace(obj, exception.ToString(), memberName);

    #endregion

    #region Operations

    #region Close session

    [Event(CloseSessionStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CloseSessionStart(string objName, string memberName) =>
            WriteEvent(CloseSessionStartId, objName, memberName);

    [Event(CloseSessionStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CloseSessionStop(string objName, string memberName) =>
        WriteEvent(CloseSessionStopId, objName, memberName);

    [NonEvent]
    public static void CloseBySendingCloseCapsuleAsyncStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void CloseBySendingCloseCapsuleAsyncCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStop(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void CloseBySendingFinAsyncStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void CloseBySendingFinAsyncCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStop(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void RequestCloseAsyncCoreStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void RequestCloseAsyncCoreCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseSessionStop(IdOf(obj), memberName ?? MissingMember);
    }

    #endregion

    #region Open outbound stream

    [Event(OpenOutboundStreamStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void OpenOutboundStreamStart(string objName, string memberName) =>
            WriteEvent(OpenOutboundStreamStartId, objName, memberName);

    [Event(OpenOutboundStreamStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void OpenOutboundStreamStop(string objName, string memberName) =>
        WriteEvent(OpenOutboundStreamStopId, objName, memberName);

    [NonEvent]
    public static void OpenOutboundStreamCoreStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.OpenOutboundStreamStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void OpenOutboundStreamCoreCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.OpenOutboundStreamStop(IdOf(obj), memberName ?? MissingMember);
    }

    #endregion

    #region Accept inbound stream

    [Event(AcceptInboundStreamStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void AcceptInboundStreamStart(string objName, string memberName) =>
            WriteEvent(AcceptInboundStreamStartId, objName, memberName);

    [Event(AcceptInboundStreamStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void AcceptInboundStreamStop(string objName, string memberName) =>
        WriteEvent(AcceptInboundStreamStopId, objName, memberName);

    [NonEvent]
    public static void AcceptInboundStreamAsyncCoreStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.AcceptInboundStreamStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void AcceptInboundStreamAsyncCoreCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.AcceptInboundStreamStop(IdOf(obj), memberName ?? MissingMember);
    }

    #endregion

    #region Capsule deserialization/processing

    [Event(CapsuleDeserializationAndProcessingStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CapsuleDeserializationAndProcessingStart(string objName, string memberName) =>
           WriteEvent(CapsuleDeserializationAndProcessingStartId, objName, memberName);

    [Event(CapsuleDeserializationAndProcessingStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CapsuleDeserializationAndProcessingStop(string objName, string memberName) =>
        WriteEvent(CapsuleDeserializationAndProcessingStopId, objName, memberName);

    [NonEvent]
    public static void CapsuleDeserializationAndProcessingStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CapsuleDeserializationAndProcessingStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void CapsuleDeserializationAndProccessingCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CapsuleDeserializationAndProcessingStop(IdOf(obj), memberName ?? MissingMember);
    }

    #endregion

    #region Send capsule

    [Event(SendCapsuleAsyncStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void SendCapsuleAsyncStart(string objName, string memberName) =>
           WriteEvent(SendCapsuleAsyncStartId, objName, memberName);

    [Event(SendCapsuleAsyncStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void SendCapsuleAsyncStop(string objName, string memberName) =>
        WriteEvent(SendCapsuleAsyncStopId, objName, memberName);

    [NonEvent]
    public static void SendCapsuleAsyncStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.SendCapsuleAsyncStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void SendCapsuleAsyncCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.SendCapsuleAsyncStop(IdOf(obj), memberName ?? MissingMember);
    }

    #endregion

    #endregion
}
