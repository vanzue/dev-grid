// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopToolbar.Services.Workspaces
{
    internal static class TemplateDefinitionValidator
    {
        private const int MaxWindows = 20;
        private const int MaxFocusRoles = 20;
        private static readonly Regex NamePattern = new("^[a-z0-9-]{3,64}$", RegexOptions.Compiled);
        private static readonly Regex RolePattern = new("^[a-z0-9-]{2,32}$", RegexOptions.Compiled);

        internal static IReadOnlyList<string> Validate(TemplateDefinition template)
        {
            var errors = new List<string>();
            if (template == null)
            {
                errors.Add("Template is null.");
                return errors;
            }

            if (template.SchemaVersion != 1)
            {
                errors.Add($"schemaVersion '{template.SchemaVersion.ToString(CultureInfo.InvariantCulture)}' is not supported.");
            }

            if (string.IsNullOrWhiteSpace(template.Name) || !NamePattern.IsMatch(template.Name))
            {
                errors.Add("name must match [a-z0-9-]{3,64}.");
            }

            if (string.IsNullOrWhiteSpace(template.DisplayName))
            {
                errors.Add("displayName is required.");
            }
            else if (template.DisplayName.Length > 80)
            {
                errors.Add("displayName exceeds max length (80).");
            }

            ValidateWindows(template.Windows, errors);
            ValidateFocusPriority(template.FocusPriority, template.Windows, errors);
            ValidateLayout(template.Layout, template.Windows, errors);

            return errors;
        }

        internal static void CanonicalizeInPlace(TemplateDefinition template)
        {
            if (template == null)
            {
                return;
            }

            template.Name = (template.Name ?? string.Empty).Trim().ToLowerInvariant();
            template.DisplayName = (template.DisplayName ?? string.Empty).Trim();
            template.Description = (template.Description ?? string.Empty).Trim();
            template.DefaultRepoRoot = (template.DefaultRepoRoot ?? string.Empty).Trim();

            if (template.FocusPriority == null)
            {
                template.FocusPriority = new List<string>();
            }
            else
            {
                for (var i = 0; i < template.FocusPriority.Count; i++)
                {
                    template.FocusPriority[i] = (template.FocusPriority[i] ?? string.Empty).Trim().ToLowerInvariant();
                }
            }

            template.Layout ??= new TemplateLayoutDefinition();
            template.Layout.Strategy = (template.Layout.Strategy ?? string.Empty).Trim();
            template.Layout.MonitorPolicy = (template.Layout.MonitorPolicy ?? string.Empty).Trim();
            template.Layout.Preset = (template.Layout.Preset ?? string.Empty).Trim();
            template.Layout.Slots ??= new List<TemplateLayoutSlotDefinition>();

            foreach (var slot in template.Layout.Slots)
            {
                if (slot == null)
                {
                    continue;
                }

                slot.Role = (slot.Role ?? string.Empty).Trim().ToLowerInvariant();
            }

            template.Windows ??= new List<TemplateWindowDefinition>();
            foreach (var window in template.Windows)
            {
                if (window == null)
                {
                    continue;
                }

                window.Role = (window.Role ?? string.Empty).Trim().ToLowerInvariant();
                window.Exe = (window.Exe ?? string.Empty).Trim();
                window.WorkingDirectory = (window.WorkingDirectory ?? string.Empty).Trim();
                window.Args = (window.Args ?? string.Empty).Trim();
                window.Init = (window.Init ?? string.Empty).Trim();
                window.Monitor = (window.Monitor ?? string.Empty).Trim();

                window.MatchHints ??= new TemplateMatchHints();
                window.MatchHints.ProcessName = (window.MatchHints.ProcessName ?? string.Empty).Trim();
                window.MatchHints.ProcessPath = (window.MatchHints.ProcessPath ?? string.Empty).Trim();
                window.MatchHints.Title = (window.MatchHints.Title ?? string.Empty).Trim();
                window.MatchHints.AppUserModelId = (window.MatchHints.AppUserModelId ?? string.Empty).Trim();
            }
        }

        private static void ValidateWindows(IReadOnlyList<TemplateWindowDefinition> windows, List<string> errors)
        {
            if (windows == null || windows.Count == 0)
            {
                errors.Add("windows must contain at least one entry.");
                return;
            }

            if (windows.Count > MaxWindows)
            {
                errors.Add($"windows exceeds max count ({MaxWindows.ToString(CultureInfo.InvariantCulture)}).");
            }

            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < windows.Count; i++)
            {
                var entry = windows[i];
                if (entry == null)
                {
                    errors.Add($"windows[{i.ToString(CultureInfo.InvariantCulture)}] is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Role) || !RolePattern.IsMatch(entry.Role))
                {
                    errors.Add($"windows[{i.ToString(CultureInfo.InvariantCulture)}].role is invalid.");
                }
                else if (!roles.Add(entry.Role))
                {
                    errors.Add($"windows[{i.ToString(CultureInfo.InvariantCulture)}].role duplicates role '{entry.Role}'.");
                }

                if (string.IsNullOrWhiteSpace(entry.Exe))
                {
                    errors.Add($"windows[{i.ToString(CultureInfo.InvariantCulture)}].exe is required.");
                }
            }
        }

        private static void ValidateFocusPriority(
            IReadOnlyList<string> focusPriority,
            IReadOnlyList<TemplateWindowDefinition> windows,
            List<string> errors)
        {
            if (focusPriority == null || focusPriority.Count == 0)
            {
                errors.Add("focusPriority must contain at least one role.");
                return;
            }

            if (focusPriority.Count > MaxFocusRoles)
            {
                errors.Add($"focusPriority exceeds max count ({MaxFocusRoles.ToString(CultureInfo.InvariantCulture)}).");
            }

            var roleSet = new HashSet<string>(
                windows?.Where(w => !string.IsNullOrWhiteSpace(w?.Role)).Select(w => w.Role)
                ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < focusPriority.Count; i++)
            {
                var role = focusPriority[i];
                if (string.IsNullOrWhiteSpace(role))
                {
                    errors.Add($"focusPriority[{i.ToString(CultureInfo.InvariantCulture)}] is empty.");
                    continue;
                }

                if (!roleSet.Contains(role))
                {
                    errors.Add($"focusPriority role '{role}' does not exist in windows.");
                }
            }
        }

        private static void ValidateLayout(
            TemplateLayoutDefinition layout,
            IReadOnlyList<TemplateWindowDefinition> windows,
            List<string> errors)
        {
            if (layout == null)
            {
                errors.Add("layout is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(layout.Strategy))
            {
                errors.Add("layout.strategy is required.");
            }

            if (string.IsNullOrWhiteSpace(layout.MonitorPolicy))
            {
                errors.Add("layout.monitorPolicy is required.");
            }

            if (layout.Slots == null || layout.Slots.Count == 0)
            {
                errors.Add("layout.slots must contain at least one entry.");
                return;
            }

            var windowRoles = new HashSet<string>(
                windows?.Where(w => !string.IsNullOrWhiteSpace(w?.Role)).Select(w => w.Role)
                ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < layout.Slots.Count; i++)
            {
                var slot = layout.Slots[i];
                if (slot == null)
                {
                    errors.Add($"layout.slots[{i.ToString(CultureInfo.InvariantCulture)}] is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(slot.Role))
                {
                    errors.Add($"layout.slots[{i.ToString(CultureInfo.InvariantCulture)}].role is required.");
                }
                else if (!windowRoles.Contains(slot.Role))
                {
                    errors.Add($"layout.slots[{i.ToString(CultureInfo.InvariantCulture)}].role '{slot.Role}' does not exist in windows.");
                }

                if (slot.X < 0 || slot.X > 1 || slot.Y < 0 || slot.Y > 1 || slot.Width <= 0 || slot.Width > 1 || slot.Height <= 0 || slot.Height > 1)
                {
                    errors.Add($"layout.slots[{i.ToString(CultureInfo.InvariantCulture)}] has out-of-range coordinates.");
                }
                else if (slot.X + slot.Width > 1 || slot.Y + slot.Height > 1)
                {
                    errors.Add($"layout.slots[{i.ToString(CultureInfo.InvariantCulture)}] exceeds normalized bounds.");
                }
            }
        }
    }
}
