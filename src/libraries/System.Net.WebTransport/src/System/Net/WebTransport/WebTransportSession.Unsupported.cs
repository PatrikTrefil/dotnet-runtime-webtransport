// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace System.Net.WebTransport;

/// <summary>
/// Represents a WebTransport session.
/// </summary>
public abstract partial class WebTransportSession
{
    /// <summary>
    /// Gets a value indicating whether WebTransport is supported on the current platform.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("osx")]
    public static bool IsSupported => false;
}
