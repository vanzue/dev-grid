// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopToolbar.Services.Workspaces
{
    internal static class TemplateDefinitionStandardizer
    {
        private static readonly string[] PreferredFocusOrder =
        {
            "editor",
            "terminal",
            "ide",
            "browser",
            "logs",
        };

        internal static void StandardizeInPlace(TemplateDefinition template)
        {
            if (template == null)
            {
                return;
            }

            TemplateDefinitionValidator.CanonicalizeInPlace(template);

            template.SchemaVersion = 1;
            template.Name = WorkspaceStoragePaths.NormalizeTemplateName(template.Name);

            if (string.IsNullOrWhiteSpace(template.DisplayName))
            {
                template.DisplayName = BuildDisplayName(template.Name);
            }

            template.Layout ??= new TemplateLayoutDefinition();
            template.Windows ??= new List<TemplateWindowDefinition>();
            template.FocusPriority ??= new List<string>();
            template.Agent ??= new TemplateAgentDefinition();
            template.Creation ??= new TemplateCreationDefinition();

            StandardizeWindows(template.Windows);

            var windowRoles = template.Windows
                .Where(w => !string.IsNullOrWhiteSpace(w?.Role))
                .Select(w => w.Role.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(template.Layout.Strategy))
            {
                template.Layout.Strategy = DetermineDefaultStrategy(windowRoles.Count);
            }

            if (string.IsNullOrWhiteSpace(template.Layout.MonitorPolicy)
                || !IsValidMonitorPolicy(template.Layout.MonitorPolicy))
            {
                template.Layout.MonitorPolicy = "primary";
            }

            if (string.IsNullOrWhiteSpace(template.Layout.Preset))
            {
                template.Layout.Preset = template.Layout.Strategy;
            }

            StandardizeSlots(template.Layout, windowRoles);
            StandardizeFocusPriority(template.FocusPriority, windowRoles);
            StandardizeAgent(template.Agent);
            StandardizeCreation(template.Creation);

            if (InferRequiresRepo(template))
            {
                template.RequiresRepo = true;
            }
        }

        internal static bool InferRequiresRepo(TemplateDefinition template)
        {
            if (template == null)
            {
                return false;
            }

            if (ContainsRepoToken(template.DefaultRepoRoot))
            {
                return true;
            }

            if (template.Creation?.CreateWorktreeByDefault == true)
            {
                return true;
            }

            if (template.Agent != null)
            {
                if (ContainsRepoToken(template.Agent.Command) || ContainsRepoToken(template.Agent.WorkingDirectory))
                {
                    return true;
                }
            }

            if (template.Windows == null)
            {
                return false;
            }

            foreach (var window in template.Windows)
            {
                if (window == null)
                {
                    continue;
                }

                if (ContainsRepoToken(window.Exe)
                    || ContainsRepoToken(window.WorkingDirectory)
                    || ContainsRepoToken(window.Args)
                    || ContainsRepoToken(window.Init))
                {
                    return true;
                }
            }

            return false;
        }

        private static void StandardizeWindows(IReadOnlyList<TemplateWindowDefinition> windows)
        {
            if (windows == null)
            {
                return;
            }

            foreach (var window in windows)
            {
                if (window == null)
                {
                    continue;
                }

                window.MatchHints ??= new TemplateMatchHints();
                if (string.IsNullOrWhiteSpace(window.MatchHints.ProcessName))
                {
                    var inferred = InferProcessNameFromExecutable(window.Exe);
                    if (!string.IsNullOrWhiteSpace(inferred))
                    {
                        window.MatchHints.ProcessName = inferred;
                    }
                }
            }
        }

        private static void StandardizeSlots(TemplateLayoutDefinition layout, IReadOnlyList<string> windowRoles)
        {
            layout.Slots ??= new List<TemplateLayoutSlotDefinition>();

            if (windowRoles == null || windowRoles.Count == 0)
            {
                layout.Slots.Clear();
                return;
            }

            var shouldRebuild = windowRoles == null || windowRoles.Count == 0 || layout.Slots.Count == 0;
            if (!shouldRebuild)
            {
                var seenRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var slot in layout.Slots)
                {
                    if (slot == null || string.IsNullOrWhiteSpace(slot.Role))
                    {
                        shouldRebuild = true;
                        break;
                    }

                    var role = slot.Role.Trim().ToLowerInvariant();
                    if (!seenRoles.Add(role))
                    {
                        shouldRebuild = true;
                        break;
                    }

                    if (!windowRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                    {
                        shouldRebuild = true;
                        break;
                    }

                    var outOfRange = slot.X < 0
                        || slot.Y < 0
                        || slot.Width <= 0
                        || slot.Height <= 0
                        || slot.X > 1
                        || slot.Y > 1
                        || slot.Width > 1
                        || slot.Height > 1
                        || slot.X + slot.Width > 1
                        || slot.Y + slot.Height > 1;
                    if (outOfRange)
                    {
                        shouldRebuild = true;
                        break;
                    }
                }

                if (seenRoles.Count != windowRoles.Count)
                {
                    shouldRebuild = true;
                }
            }

            if (shouldRebuild)
            {
                layout.Slots = WorkspaceLayoutEngine.BuildSlots(layout.Strategy, windowRoles);
                return;
            }

            var slotByRole = new Dictionary<string, TemplateLayoutSlotDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in layout.Slots)
            {
                if (slot == null || string.IsNullOrWhiteSpace(slot.Role))
                {
                    continue;
                }

                var role = slot.Role.Trim().ToLowerInvariant();
                if (!slotByRole.ContainsKey(role))
                {
                    slotByRole[role] = slot;
                }
            }

            layout.Slots = windowRoles
                .Where(role => slotByRole.ContainsKey(role))
                .Select(role => slotByRole[role])
                .ToList();
        }

        private static void StandardizeFocusPriority(List<string> focusPriority, IReadOnlyList<string> windowRoles)
        {
            var normalized = new List<string>();
            if (focusPriority != null)
            {
                foreach (var role in focusPriority)
                {
                    if (string.IsNullOrWhiteSpace(role))
                    {
                        continue;
                    }

                    var candidate = role.Trim().ToLowerInvariant();
                    if (!windowRoles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        normalized.Add(candidate);
                    }
                }
            }

            foreach (var preferred in PreferredFocusOrder)
            {
                if (windowRoles.Contains(preferred, StringComparer.OrdinalIgnoreCase)
                    && !normalized.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(preferred);
                }
            }

            foreach (var role in windowRoles)
            {
                if (!normalized.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(role);
                }
            }

            focusPriority.Clear();
            focusPriority.AddRange(normalized);
        }

        private static void StandardizeAgent(TemplateAgentDefinition agent)
        {
            if (agent == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                agent.Name = "copilot";
            }

            if (string.IsNullOrWhiteSpace(agent.WorkingDirectory))
            {
                agent.WorkingDirectory = "{repo}";
            }

            if (agent.Enabled && string.IsNullOrWhiteSpace(agent.Command))
            {
                agent.Command = string.IsNullOrWhiteSpace(agent.Name) ? "copilot" : agent.Name;
            }
        }

        private static void StandardizeCreation(TemplateCreationDefinition creation)
        {
            if (creation == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(creation.WorktreeBaseBranch))
            {
                creation.WorktreeBaseBranch = "main";
            }
        }

        internal static string InferProcessNameFromExecutable(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe) || exe.Contains("{", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            try
            {
                var candidate = exe.Trim().Trim('"');
                if (!exe.TrimStart().StartsWith("\"", StringComparison.Ordinal) && !File.Exists(candidate))
                {
                    var firstSpace = candidate.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        candidate = candidate[..firstSpace];
                    }
                }

                var fileName = Path.GetFileNameWithoutExtension(candidate);
                return string.IsNullOrWhiteSpace(fileName)
                    ? string.Empty
                    : fileName.Trim().ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DetermineDefaultStrategy(int windowCount)
        {
            if (windowCount <= 1)
            {
                return "single";
            }

            if (windowCount == 2)
            {
                return "side-by-side";
            }

            if (windowCount == 3)
            {
                return "main-left-70";
            }

            return "grid";
        }

        private static bool ContainsRepoToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("{repo}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsValidMonitorPolicy(string monitorPolicy)
        {
            var policy = (monitorPolicy ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(policy))
            {
                return false;
            }

            if (policy.StartsWith("explicit:", StringComparison.OrdinalIgnoreCase))
            {
                return policy.Length > "explicit:".Length;
            }

            return string.Equals(policy, "primary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(policy, "any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(policy, "current", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDisplayName(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return "Workspace";
            }

            var parts = templateName.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "Workspace";
            }

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                parts[i] = part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
            }

            return string.Join(" ", parts);
        }
    }
}
