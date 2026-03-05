// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateUpdateDocument
    {
        [JsonPropertyName("syntax")]
        public string Syntax { get; set; } = "workspace-template-update/v1";

        [JsonPropertyName("template")]
        public string Template { get; set; } = string.Empty;

        [JsonPropertyName("ops")]
        public List<TemplateUpdateOperation> Ops { get; set; } = new();
    }

    internal sealed class TemplateUpdateOperation
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }

    internal sealed class TemplateUpdateApplyResult
    {
        public bool Success { get; set; }

        public List<string> Errors { get; set; } = new();
    }

    internal static class TemplateUpdateEngine
    {
        internal static TemplateUpdateApplyResult ApplyInPlace(
            TemplateDefinition template,
            IReadOnlyList<TemplateUpdateOperation> operations)
        {
            var result = new TemplateUpdateApplyResult();
            if (template == null)
            {
                result.Errors.Add("Template is null.");
                return result;
            }

            if (operations == null || operations.Count == 0)
            {
                result.Success = true;
                return result;
            }

            template.Windows ??= new List<TemplateWindowDefinition>();

            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op == null)
                {
                    result.Errors.Add($"ops[{i}] is null.");
                    continue;
                }

                var opName = (op.Op ?? string.Empty).Trim().ToLowerInvariant();
                var path = (op.Path ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(opName))
                {
                    result.Errors.Add($"ops[{i}].op is required.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    result.Errors.Add($"ops[{i}].path is required.");
                    continue;
                }

                var opResult = opName switch
                {
                    "set" => ApplySet(template, path, op.Value, allowCreateWindow: true),
                    "replace" => ApplySet(template, path, op.Value, allowCreateWindow: false),
                    "merge" => ApplySet(template, path, op.Value, allowCreateWindow: true),
                    "upsert" => ApplyUpsert(template, path, op.Value),
                    "remove" => ApplyRemove(template, path),
                    _ => $"Unsupported op '{opName}'.",
                };

                if (!string.IsNullOrWhiteSpace(opResult))
                {
                    result.Errors.Add($"ops[{i}]: {opResult}");
                }
            }

            result.Success = result.Errors.Count == 0;
            return result;
        }

        private static string ApplySet(TemplateDefinition template, string path, JsonElement value, bool allowCreateWindow)
        {
            if (TryParseWindowPath(path, out var role, out var windowPath))
            {
                var window = GetOrCreateWindow(template, role, allowCreateWindow);
                if (window == null)
                {
                    return $"Window role '{role}' does not exist.";
                }

                if (string.IsNullOrWhiteSpace(windowPath))
                {
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return "Setting a full window entry requires object value.";
                    }

                    var parsed = JsonSerializer.Deserialize<TemplateWindowDefinition>(value.GetRawText());
                    if (parsed == null)
                    {
                        return "Window object value is invalid.";
                    }

                    parsed.Role = role;
                    ReplaceWindow(template, role, parsed);
                    return string.Empty;
                }

                return ApplyWindowPropertySet(window, windowPath, value);
            }

            switch (path.ToLowerInvariant())
            {
                case "name":
                    template.Name = ReadString(value);
                    return string.Empty;
                case "displayname":
                    template.DisplayName = ReadString(value);
                    return string.Empty;
                case "description":
                    template.Description = ReadString(value);
                    return string.Empty;
                case "requiresrepo":
                    template.RequiresRepo = ReadBool(value);
                    return string.Empty;
                case "defaultreporoot":
                    template.DefaultRepoRoot = ReadString(value);
                    return string.Empty;
                case "focuspriority":
                    template.FocusPriority = ReadStringList(value);
                    return string.Empty;
                case "layout":
                    return SetLayoutObject(template, value);
                case "layout.strategy":
                    template.Layout ??= new TemplateLayoutDefinition();
                    template.Layout.Strategy = ReadString(value);
                    return string.Empty;
                case "layout.monitorpolicy":
                    template.Layout ??= new TemplateLayoutDefinition();
                    template.Layout.MonitorPolicy = ReadString(value);
                    return string.Empty;
                case "layout.preset":
                    template.Layout ??= new TemplateLayoutDefinition();
                    template.Layout.Preset = ReadString(value);
                    return string.Empty;
                case "agent":
                    return SetAgentObject(template, value);
                case "agent.enabled":
                    template.Agent ??= new TemplateAgentDefinition();
                    template.Agent.Enabled = ReadBool(value);
                    return string.Empty;
                case "agent.name":
                    template.Agent ??= new TemplateAgentDefinition();
                    template.Agent.Name = ReadString(value);
                    return string.Empty;
                case "agent.command":
                    template.Agent ??= new TemplateAgentDefinition();
                    template.Agent.Command = ReadString(value);
                    return string.Empty;
                case "agent.workingdirectory":
                    template.Agent ??= new TemplateAgentDefinition();
                    template.Agent.WorkingDirectory = ReadString(value);
                    return string.Empty;
                case "creation":
                    return SetCreationObject(template, value);
                case "creation.createworktreebydefault":
                    template.Creation ??= new TemplateCreationDefinition();
                    template.Creation.CreateWorktreeByDefault = ReadBool(value);
                    return string.Empty;
                case "creation.worktreebasebranch":
                    template.Creation ??= new TemplateCreationDefinition();
                    template.Creation.WorktreeBaseBranch = ReadString(value);
                    return string.Empty;
                default:
                    return $"Unsupported path '{path}'.";
            }
        }

        private static string ApplyUpsert(TemplateDefinition template, string path, JsonElement value)
        {
            if (!TryParseWindowPath(path, out var role, out var windowPath))
            {
                return $"upsert currently supports only windows selector paths. path='{path}'.";
            }

            if (!string.IsNullOrWhiteSpace(windowPath))
            {
                var window = GetOrCreateWindow(template, role, allowCreateWindow: true);
                return ApplyWindowPropertySet(window, windowPath, value);
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                return "upsert windows entry requires object value.";
            }

            var parsed = JsonSerializer.Deserialize<TemplateWindowDefinition>(value.GetRawText());
            if (parsed == null)
            {
                return "Window object value is invalid.";
            }

            parsed.Role = role;
            ReplaceWindow(template, role, parsed);
            return string.Empty;
        }

        private static string ApplyRemove(TemplateDefinition template, string path)
        {
            if (TryParseWindowPath(path, out var role, out var windowPath))
            {
                var window = GetOrCreateWindow(template, role, allowCreateWindow: false);
                if (window == null)
                {
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(windowPath))
                {
                    template.Windows = template.Windows
                        .Where(entry => !string.Equals(entry?.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    return string.Empty;
                }

                return ApplyWindowPropertyRemove(window, windowPath);
            }

            switch (path.ToLowerInvariant())
            {
                case "description":
                    template.Description = string.Empty;
                    return string.Empty;
                case "defaultreporoot":
                    template.DefaultRepoRoot = string.Empty;
                    return string.Empty;
                case "focuspriority":
                    template.FocusPriority = new List<string>();
                    return string.Empty;
                case "layout.preset":
                    template.Layout ??= new TemplateLayoutDefinition();
                    template.Layout.Preset = string.Empty;
                    return string.Empty;
                case "agent.command":
                    template.Agent ??= new TemplateAgentDefinition();
                    template.Agent.Command = string.Empty;
                    return string.Empty;
                default:
                    return $"Unsupported remove path '{path}'.";
            }
        }

        private static string SetLayoutObject(TemplateDefinition template, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                return "layout requires object value.";
            }

            var layout = JsonSerializer.Deserialize<TemplateLayoutDefinition>(value.GetRawText());
            if (layout == null)
            {
                return "layout object value is invalid.";
            }

            template.Layout = layout;
            return string.Empty;
        }

        private static string SetAgentObject(TemplateDefinition template, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                return "agent requires object value.";
            }

            var agent = JsonSerializer.Deserialize<TemplateAgentDefinition>(value.GetRawText());
            if (agent == null)
            {
                return "agent object value is invalid.";
            }

            template.Agent = agent;
            return string.Empty;
        }

        private static string SetCreationObject(TemplateDefinition template, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                return "creation requires object value.";
            }

            var creation = JsonSerializer.Deserialize<TemplateCreationDefinition>(value.GetRawText());
            if (creation == null)
            {
                return "creation object value is invalid.";
            }

            template.Creation = creation;
            return string.Empty;
        }

        private static bool TryParseWindowPath(string path, out string role, out string windowPath)
        {
            role = string.Empty;
            windowPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            const string prefix = "windows[role=";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var closeIndex = path.IndexOf(']', prefix.Length);
            if (closeIndex <= prefix.Length)
            {
                return false;
            }

            role = path.Substring(prefix.Length, closeIndex - prefix.Length).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(role))
            {
                return false;
            }

            if (path.Length > closeIndex + 1)
            {
                if (path[closeIndex + 1] != '.')
                {
                    return false;
                }

                windowPath = path[(closeIndex + 2)..].Trim();
            }

            return true;
        }

        private static TemplateWindowDefinition GetOrCreateWindow(TemplateDefinition template, string role, bool allowCreateWindow)
        {
            var existing = template.Windows.FirstOrDefault(entry =>
                entry != null && string.Equals(entry.Role, role, StringComparison.OrdinalIgnoreCase));
            if (existing != null || !allowCreateWindow)
            {
                return existing;
            }

            var created = new TemplateWindowDefinition
            {
                Role = role,
                MatchHints = new TemplateMatchHints(),
            };
            template.Windows.Add(created);
            return created;
        }

        private static void ReplaceWindow(TemplateDefinition template, string role, TemplateWindowDefinition replacement)
        {
            replacement.Role = role;
            replacement.MatchHints ??= new TemplateMatchHints();
            for (var i = 0; i < template.Windows.Count; i++)
            {
                var candidate = template.Windows[i];
                if (candidate != null && string.Equals(candidate.Role, role, StringComparison.OrdinalIgnoreCase))
                {
                    template.Windows[i] = replacement;
                    return;
                }
            }

            template.Windows.Add(replacement);
        }

        private static string ApplyWindowPropertySet(TemplateWindowDefinition window, string path, JsonElement value)
        {
            window.MatchHints ??= new TemplateMatchHints();
            switch (path.ToLowerInvariant())
            {
                case "role":
                    window.Role = ReadString(value);
                    return string.Empty;
                case "exe":
                    window.Exe = ReadString(value);
                    return string.Empty;
                case "cwd":
                case "workingdirectory":
                    window.WorkingDirectory = ReadString(value);
                    return string.Empty;
                case "args":
                    window.Args = ReadString(value);
                    return string.Empty;
                case "init":
                    window.Init = ReadString(value);
                    return string.Empty;
                case "monitor":
                    window.Monitor = ReadString(value);
                    return string.Empty;
                case "matchhints":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return "matchHints requires object value.";
                    }

                    var matchHints = JsonSerializer.Deserialize<TemplateMatchHints>(value.GetRawText());
                    if (matchHints == null)
                    {
                        return "matchHints object value is invalid.";
                    }

                    window.MatchHints = matchHints;
                    return string.Empty;
                case "matchhints.processname":
                    window.MatchHints.ProcessName = ReadString(value);
                    return string.Empty;
                case "matchhints.processpath":
                    window.MatchHints.ProcessPath = ReadString(value);
                    return string.Empty;
                case "matchhints.title":
                    window.MatchHints.Title = ReadString(value);
                    return string.Empty;
                case "matchhints.appusermodelid":
                    window.MatchHints.AppUserModelId = ReadString(value);
                    return string.Empty;
                default:
                    return $"Unsupported window path '{path}'.";
            }
        }

        private static string ApplyWindowPropertyRemove(TemplateWindowDefinition window, string path)
        {
            window.MatchHints ??= new TemplateMatchHints();
            switch (path.ToLowerInvariant())
            {
                case "exe":
                    window.Exe = string.Empty;
                    return string.Empty;
                case "cwd":
                case "workingdirectory":
                    window.WorkingDirectory = string.Empty;
                    return string.Empty;
                case "args":
                    window.Args = string.Empty;
                    return string.Empty;
                case "init":
                    window.Init = string.Empty;
                    return string.Empty;
                case "monitor":
                    window.Monitor = string.Empty;
                    return string.Empty;
                case "matchhints":
                    window.MatchHints = new TemplateMatchHints();
                    return string.Empty;
                case "matchhints.processname":
                    window.MatchHints.ProcessName = string.Empty;
                    return string.Empty;
                case "matchhints.processpath":
                    window.MatchHints.ProcessPath = string.Empty;
                    return string.Empty;
                case "matchhints.title":
                    window.MatchHints.Title = string.Empty;
                    return string.Empty;
                case "matchhints.appusermodelid":
                    window.MatchHints.AppUserModelId = string.Empty;
                    return string.Empty;
                default:
                    return $"Unsupported window remove path '{path}'.";
            }
        }

        private static string ReadString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Undefined => string.Empty,
                JsonValueKind.False => "false",
                JsonValueKind.True => "true",
                JsonValueKind.Number => element.ToString(),
                _ => string.Empty,
            };
        }

        private static bool ReadBool(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) && parsed,
                JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
                _ => false,
            };
        }

        private static List<string> ReadStringList(JsonElement element)
        {
            var values = new List<string>();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value.Trim());
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var raw = element.GetString() ?? string.Empty;
                values.AddRange(raw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item)));
            }

            return values;
        }
    }
}
