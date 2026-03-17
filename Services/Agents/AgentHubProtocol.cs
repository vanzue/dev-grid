// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Agents
{
    internal static class AgentHubProtocol
    {
        internal const string PipeName = "PowerToys.AgentHub";
    }

    internal sealed class AgentHubEventEnvelope
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }

    internal sealed class AgentRunOptions
    {
        public string SessionId { get; set; } = string.Empty;

        public string TemplateId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Backend { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        public string CommandLine { get; set; } = string.Empty;

        public string InitialInput { get; set; } = string.Empty;

        public bool HoldOpen { get; set; } = true;

        public IReadOnlyList<string> WaitLiterals { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> WaitRegexPatterns { get; set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
