// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateSuggestionRequest
    {
        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public bool? RequiresRepo { get; set; }

        public string DefaultRepoRoot { get; set; } = string.Empty;

        public bool EnableAgent { get; set; } = true;

        public string AgentName { get; set; } = "copilot";

        public string AgentCommand { get; set; } = string.Empty;

        public string AgentWorkingDirectory { get; set; } = "{repo}";

        public string LayoutStrategy { get; set; } = string.Empty;

        public string MonitorPolicy { get; set; } = "primary";

        public bool CreateWorktreeByDefault { get; set; }

        public string WorktreeBaseBranch { get; set; } = "main";

        public List<TemplateSuggestionAppInput> Apps { get; set; } = new();
    }

    internal sealed class TemplateSuggestionAppInput
    {
        public bool Enabled { get; set; } = true;

        public string App { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        public string Cwd { get; set; } = string.Empty;

        public string Args { get; set; } = string.Empty;

        public string Init { get; set; } = string.Empty;

        public string Monitor { get; set; } = string.Empty;

        public string ProcessName { get; set; } = string.Empty;
    }

    internal sealed class TemplateSuggestionResult
    {
        public TemplateDefinition Template { get; set; } = new();

        public List<string> ValidationErrors { get; set; } = new();

        public List<ExecutableLocator.Resolution> ResolvedApps { get; set; } = new();
    }

    internal static class TemplateSuggestionService
    {
        internal static TemplateSuggestionResult BuildSuggestion(TemplateSuggestionRequest request)
        {
            request ??= new TemplateSuggestionRequest();

            var normalizedName = NormalizeTemplateName(
                request.Name,
                string.IsNullOrWhiteSpace(request.DisplayName) ? "workspace-template" : request.DisplayName);
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? BuildDisplayName(normalizedName)
                : request.DisplayName.Trim();

            var appInputs = BuildAppInputs(request);
            var windows = new List<TemplateWindowDefinition>();
            var resolvedApps = new List<ExecutableLocator.Resolution>();
            var usedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in appInputs)
            {
                if (app == null || !app.Enabled || string.IsNullOrWhiteSpace(app.App))
                {
                    continue;
                }

                var resolved = ExecutableLocator.Resolve(app.App);
                resolvedApps.Add(resolved);

                var baseRole = NormalizeRole(string.IsNullOrWhiteSpace(app.Role) ? resolved.SuggestedRole : app.Role);
                var role = MakeUniqueRole(baseRole, usedRoles);
                windows.Add(new TemplateWindowDefinition
                {
                    Role = role,
                    Exe = string.IsNullOrWhiteSpace(resolved.Resolved) ? resolved.Requested : resolved.Resolved,
                    WorkingDirectory = string.IsNullOrWhiteSpace(app.Cwd) ? resolved.DefaultWorkingDirectory : app.Cwd.Trim(),
                    Args = string.IsNullOrWhiteSpace(app.Args) ? resolved.DefaultArgs : app.Args.Trim(),
                    Init = app.Init?.Trim() ?? string.Empty,
                    Monitor = app.Monitor?.Trim() ?? string.Empty,
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = string.IsNullOrWhiteSpace(app.ProcessName)
                            ? TemplateDefinitionStandardizer.InferProcessNameFromExecutable(resolved.Resolved)
                            : app.ProcessName.Trim().ToLowerInvariant(),
                    },
                });
            }

            if (windows.Count == 0)
            {
                var fallback = ExecutableLocator.Resolve("vscode");
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "editor",
                    Exe = fallback.Exists ? fallback.Resolved : "code.exe",
                    WorkingDirectory = "{repo}",
                    Args = "--new-window {repo}",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "code",
                    },
                });

                resolvedApps.Add(fallback);
            }

            var roles = windows
                .Select(window => window.Role)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var layoutStrategy = string.IsNullOrWhiteSpace(request.LayoutStrategy)
                ? DetermineDefaultStrategy(roles.Count)
                : request.LayoutStrategy.Trim();
            var monitorPolicy = string.IsNullOrWhiteSpace(request.MonitorPolicy)
                ? "primary"
                : request.MonitorPolicy.Trim();

            var template = new TemplateDefinition
            {
                SchemaVersion = 1,
                Name = normalizedName,
                DisplayName = displayName,
                Description = request.Description?.Trim() ?? string.Empty,
                RequiresRepo = request.RequiresRepo ?? false,
                DefaultRepoRoot = request.DefaultRepoRoot?.Trim() ?? string.Empty,
                FocusPriority = BuildFocusPriority(roles),
                Layout = new TemplateLayoutDefinition
                {
                    Strategy = layoutStrategy,
                    MonitorPolicy = monitorPolicy,
                    Preset = layoutStrategy,
                    Slots = WorkspaceLayoutEngine.BuildSlots(layoutStrategy, roles),
                },
                Windows = windows,
                Agent = new TemplateAgentDefinition
                {
                    Enabled = request.EnableAgent,
                    Name = string.IsNullOrWhiteSpace(request.AgentName) ? "copilot" : request.AgentName.Trim().ToLowerInvariant(),
                    Command = request.AgentCommand?.Trim() ?? string.Empty,
                    WorkingDirectory = string.IsNullOrWhiteSpace(request.AgentWorkingDirectory)
                        ? "{repo}"
                        : request.AgentWorkingDirectory.Trim(),
                },
                Creation = new TemplateCreationDefinition
                {
                    CreateWorktreeByDefault = request.CreateWorktreeByDefault,
                    WorktreeBaseBranch = string.IsNullOrWhiteSpace(request.WorktreeBaseBranch)
                        ? "main"
                        : request.WorktreeBaseBranch.Trim(),
                },
            };

            TemplateDefinitionStandardizer.StandardizeInPlace(template);
            if (request.RequiresRepo.HasValue)
            {
                template.RequiresRepo = request.RequiresRepo.Value;
            }

            return new TemplateSuggestionResult
            {
                Template = template,
                ValidationErrors = TemplateDefinitionValidator.Validate(template).ToList(),
                ResolvedApps = resolvedApps,
            };
        }

        private static string MakeUniqueRole(string role, HashSet<string> usedRoles)
        {
            var baseRole = string.IsNullOrWhiteSpace(role) ? "app" : role;
            if (usedRoles.Add(baseRole))
            {
                return baseRole;
            }

            for (var i = 2; i < 100; i++)
            {
                var candidate = $"{baseRole}-{i}";
                if (candidate.Length > 32)
                {
                    candidate = candidate[..32].Trim('-');
                }

                if (usedRoles.Add(candidate))
                {
                    return candidate;
                }
            }

            return baseRole;
        }

        private static List<TemplateSuggestionAppInput> BuildAppInputs(TemplateSuggestionRequest request)
        {
            if (request.Apps != null && request.Apps.Count > 0)
            {
                return request.Apps;
            }

            var defaults = new List<TemplateSuggestionAppInput>
            {
                new()
                {
                    App = "vscode",
                    Role = "editor",
                    Cwd = "{repo}",
                    Args = "--new-window {repo}",
                },
                new()
                {
                    App = "terminal",
                    Role = "terminal",
                    Cwd = "{repo}",
                },
                new()
                {
                    App = "explorer",
                    Role = "logs",
                    Cwd = "{repo}\\logs",
                    Args = "/n, \"{repo}\\logs\"",
                },
            };

            if (!request.EnableAgent)
            {
                defaults[1].Enabled = false;
            }

            return defaults;
        }

        private static List<string> BuildFocusPriority(IReadOnlyList<string> roles)
        {
            var focus = new List<string>();
            var preferred = new[] { "editor", "terminal", "ide", "browser", "logs" };
            foreach (var item in preferred)
            {
                if (roles.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    focus.Add(item);
                }
            }

            foreach (var role in roles)
            {
                if (!focus.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    focus.Add(role);
                }
            }

            return focus;
        }

        private static string NormalizeTemplateName(string value, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
            source = source.Trim().ToLowerInvariant();

            var chars = new List<char>(source.Length);
            var previousDash = false;
            foreach (var ch in source)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    chars.Add(ch);
                    previousDash = false;
                    continue;
                }

                if (!previousDash)
                {
                    chars.Add('-');
                    previousDash = true;
                }
            }

            var normalized = new string(chars.ToArray()).Trim('-');
            if (normalized.Length < 3)
            {
                normalized = "workspace-template";
            }

            if (normalized.Length > 64)
            {
                normalized = normalized[..64].Trim('-');
            }

            return WorkspaceStoragePaths.NormalizeTemplateName(normalized);
        }

        private static string NormalizeRole(string value)
        {
            var source = string.IsNullOrWhiteSpace(value) ? "app" : value;
            source = source.Trim().ToLowerInvariant();

            var chars = new List<char>(source.Length);
            var previousDash = false;
            foreach (var ch in source)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    chars.Add(ch);
                    previousDash = false;
                    continue;
                }

                if (!previousDash)
                {
                    chars.Add('-');
                    previousDash = true;
                }
            }

            var normalized = new string(chars.ToArray()).Trim('-');
            if (normalized.Length < 2)
            {
                normalized = "app";
            }

            if (normalized.Length > 32)
            {
                normalized = normalized[..32].Trim('-');
            }

            return normalized;
        }

        private static string BuildDisplayName(string templateName)
        {
            var parts = (templateName ?? string.Empty)
                .Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "Workspace";
            }

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                parts[i] = part.Length == 0
                    ? part
                    : char.ToUpperInvariant(part[0]) + part[1..];
            }

            return string.Join(" ", parts);
        }

        private static string DetermineDefaultStrategy(int count)
        {
            if (count <= 1)
            {
                return "single";
            }

            if (count == 2)
            {
                return "side-by-side";
            }

            if (count == 3)
            {
                return "main-left-70";
            }

            return "grid";
        }

    }
}
