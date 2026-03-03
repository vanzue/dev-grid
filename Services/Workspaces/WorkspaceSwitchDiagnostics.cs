// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceSwitchDiagnostics
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("workspaceId")]
        public string WorkspaceId { get; set; } = string.Empty;

        [JsonPropertyName("claimedCount")]
        public int ClaimedCount { get; set; }

        [JsonPropertyName("launchedCount")]
        public int LaunchedCount { get; set; }

        [JsonPropertyName("arrangedCount")]
        public int ArrangedCount { get; set; }

        [JsonPropertyName("minimizedCount")]
        public int MinimizedCount { get; set; }

        [JsonPropertyName("focusedRole")]
        public string FocusedRole { get; set; } = string.Empty;

        [JsonPropertyName("focusedHwnd")]
        public string FocusedHwnd { get; set; } = string.Empty;

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("stageDurationsMs")]
        public Dictionary<string, long> StageDurationsMs { get; set; } = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new();
    }
}
