// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Services.Everything;

public sealed class RecentSearchEntry
{
    public string FullPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsFolder { get; set; }

    public DateTime LastUsedUtc { get; set; }

    public int UseCount { get; set; }
}
