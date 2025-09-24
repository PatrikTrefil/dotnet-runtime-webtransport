// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Test.Common
{
    public sealed record Http3SettingsEntry()
    {
        public Http3SettingType SettingId { get; init; }
        public long Value { get; init; }
    }
}
