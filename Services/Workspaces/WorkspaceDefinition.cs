// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Workspaces
{
    public sealed class WorkspaceDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("template-name")]
        public string TemplateName { get; set; } = string.Empty;

        [JsonPropertyName("instance-name")]
        public string InstanceName { get; set; } = string.Empty;

        [JsonPropertyName("workspace-title")]
        public string WorkspaceTitle { get; set; } = string.Empty;

        [JsonPropertyName("repo-root")]
        public string RepoRoot { get; set; } = string.Empty;

        [JsonPropertyName("focus-priority")]
        public List<string> FocusPriority { get; set; } = new();

        [JsonPropertyName("focused-application-id")]
        public string FocusedApplicationId { get; set; } = string.Empty;

        [JsonPropertyName("creation-time")]
        public long CreationTime { get; set; }

        [JsonPropertyName("last-launched-time")]
        public long? LastLaunchedTime { get; set; }

        [JsonPropertyName("is-shortcut-needed")]
        public bool IsShortcutNeeded { get; set; }

        [JsonPropertyName("move-existing-windows")]
        public bool MoveExistingWindows { get; set; } = true;

        [JsonPropertyName("runtime-session-only")]
        public bool RuntimeSessionOnly { get; set; }

        [JsonPropertyName("runtime-session-id")]
        public string RuntimeSessionId { get; set; } = string.Empty;

        [JsonPropertyName("monitor-configuration")]
        public List<MonitorDefinition> Monitors { get; set; } = new();

        [JsonPropertyName("applications")]
        public List<ApplicationDefinition> Applications { get; set; } = new();
    }
}
