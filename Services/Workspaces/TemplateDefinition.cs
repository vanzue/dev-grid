// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateDefinition
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("requiresRepo")]
        public bool RequiresRepo { get; set; }

        [JsonPropertyName("defaultRepoRoot")]
        public string DefaultRepoRoot { get; set; } = string.Empty;

        [JsonPropertyName("focusPriority")]
        public List<string> FocusPriority { get; set; } = new();

        [JsonPropertyName("layout")]
        public TemplateLayoutDefinition Layout { get; set; } = new();

        [JsonPropertyName("windows")]
        public List<TemplateWindowDefinition> Windows { get; set; } = new();

        [JsonPropertyName("agent")]
        public TemplateAgentDefinition Agent { get; set; } = new();

        [JsonPropertyName("creation")]
        public TemplateCreationDefinition Creation { get; set; } = new();
    }

    internal sealed class TemplateLayoutDefinition
    {
        [JsonPropertyName("strategy")]
        public string Strategy { get; set; } = string.Empty;

        [JsonPropertyName("monitorPolicy")]
        public string MonitorPolicy { get; set; } = string.Empty;

        [JsonPropertyName("preset")]
        public string Preset { get; set; } = string.Empty;

        [JsonPropertyName("slots")]
        public List<TemplateLayoutSlotDefinition> Slots { get; set; } = new();
    }

    internal sealed class TemplateLayoutSlotDefinition
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("w")]
        public double Width { get; set; }

        [JsonPropertyName("h")]
        public double Height { get; set; }

        [JsonPropertyName("minWidth")]
        public int? MinWidth { get; set; }

        [JsonPropertyName("minHeight")]
        public int? MinHeight { get; set; }
    }

    internal sealed class TemplateWindowDefinition
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("exe")]
        public string Exe { get; set; } = string.Empty;

        [JsonPropertyName("cwd")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("args")]
        public string Args { get; set; } = string.Empty;

        [JsonPropertyName("init")]
        public string Init { get; set; } = string.Empty;

        [JsonPropertyName("monitor")]
        public string Monitor { get; set; } = string.Empty;

        [JsonPropertyName("matchHints")]
        public TemplateMatchHints MatchHints { get; set; } = new();
    }

    internal sealed class TemplateMatchHints
    {
        [JsonPropertyName("processName")]
        public string ProcessName { get; set; } = string.Empty;

        [JsonPropertyName("processPath")]
        public string ProcessPath { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("appUserModelId")]
        public string AppUserModelId { get; set; } = string.Empty;
    }

    internal sealed class TemplateAgentDefinition
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "copilot";

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = "{repo}";
    }

    internal sealed class TemplateCreationDefinition
    {
        [JsonPropertyName("createWorktreeByDefault")]
        public bool CreateWorktreeByDefault { get; set; }

        [JsonPropertyName("worktreeBaseBranch")]
        public string WorktreeBaseBranch { get; set; } = "main";
    }
}
