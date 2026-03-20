// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Test.Common
{
        public enum Http3SettingType : long
    {
        /// <summary>
        /// SETTINGS_QPACK_MAX_TABLE_CAPACITY
        /// The maximum dynamic table size. The default is 0.
        /// https://tools.ietf.org/html/draft-ietf-quic-qpack-11#section-5
        /// </summary>
        QPackMaxTableCapacity = 0x1,

        // Below are explicitly reserved and should never be sent, per
        // https://tools.ietf.org/html/draft-ietf-quic-http-31#section-7.2.4.1
        // and
        // https://tools.ietf.org/html/draft-ietf-quic-http-31#section-11.2.2
        ReservedHttp2EnablePush = 0x2,
        ReservedHttp2MaxConcurrentStreams = 0x3,
        ReservedHttp2InitialWindowSize = 0x4,
        ReservedHttp2MaxFrameSize = 0x5,

        /// <summary>
        /// SETTINGS_MAX_HEADER_LIST_SIZE
        /// The maximum size of headers. The default is unlimited.
        /// https://tools.ietf.org/html/draft-ietf-quic-http-24#section-7.2.4.1
        /// </summary>
        MaxHeaderListSize = 0x6,

        /// <summary>
        /// SETTINGS_QPACK_BLOCKED_STREAMS
        /// The maximum number of request streams that can be blocked waiting for QPack instructions. The default is 0.
        /// https://tools.ietf.org/html/draft-ietf-quic-qpack-11#section-5
        /// </summary>
        QPackBlockedStreams = 0x7,

        /// <summary>
        /// SETTINGS_ENABLE_CONNECT_PROTOCOL
        /// Value 1 indicates support for the Extended CONNECT
        /// https://www.rfc-editor.org/rfc/rfc9220#section-5-2.4.1
        /// </summary>
        EnableConnect = 0x8,

        /// <summary>
        /// The SETTINGS_WEBTRANSPORT_MAX_SESSIONS
        /// Indicates that the specified HTTP/3 endpoint is WebTransport-capable and the number of concurrent sessions it is willing to receive.
        /// </summary>
        /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-9.2-2"/>
        WebTransportMaxSessions = 0xc671706a,

        /// <summary>
        /// SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI
        /// Indicates the initial value for the unidirectional max stream limit for WebTransport sessions.
        /// </summary>
        /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_UNI"/>
        WebTransportInitialMaxUnidirectionalStreamsPerSession = 0x2b64,

        /// <summary>
        /// SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI
        /// Indicates the initial value for the bidirectional max stream limit for WebTransport sessions.
        /// </summary>
        /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#SETTINGS_WEBTRANSPORT_INITIAL_MAX_STREAMS_BIDI"/>
        WebTransportInitialMaxBidirectionalStreamsPerSession = 0x2b65,

        /// <summary>
        /// SETTINGS_WEBTRANSPORT_INITIAL_MAX_DATA
        /// Indicates the initial value for the session data limit for WebTransport sessions.
        /// </summary>
        /// <seealso href="https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-12#section-9.2-12.2.1"/>
        WebTransportInitialMaxDataPerSession = 0x2b61,

        /// <summary>
        /// H3_DATAGRAM, default is 0 (off)
        /// indicates that the server suppprts sending individual datagrams over Http/3
        /// rather than just streams.
        /// </summary>
        H3Datagram = 0xffd277
    }
}
