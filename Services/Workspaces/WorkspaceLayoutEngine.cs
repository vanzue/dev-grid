// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using TopToolbar.Services.Display;

namespace TopToolbar.Services.Workspaces
{
    internal static class WorkspaceLayoutEngine
    {
        internal readonly struct LayoutRect
        {
            public LayoutRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public int X { get; }

            public int Y { get; }

            public int Width { get; }

            public int Height { get; }

            public int Right => X + Width;

            public int Bottom => Y + Height;
        }

        internal static DisplayMonitor ResolveMonitor(
            IReadOnlyList<DisplayMonitor> monitors,
            string globalPolicy,
            string windowPolicy)
        {
            if (monitors == null || monitors.Count == 0)
            {
                return null;
            }

            var explicitPolicy = !string.IsNullOrWhiteSpace(windowPolicy) ? windowPolicy : globalPolicy;
            var policy = (explicitPolicy ?? string.Empty).Trim();

            if (policy.StartsWith("explicit:", StringComparison.OrdinalIgnoreCase))
            {
                var id = policy.Substring("explicit:".Length).Trim();
                var explicitMatch = monitors.FirstOrDefault(m =>
                    string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                if (explicitMatch != null)
                {
                    return explicitMatch;
                }
            }

            if (string.Equals(policy, "any", StringComparison.OrdinalIgnoreCase))
            {
                return monitors
                    .OrderByDescending(m => (long)m.Bounds.Width * m.Bounds.Height)
                    .ThenBy(m => m.Index)
                    .FirstOrDefault();
            }

            // 'primary' and 'current' both resolve to lowest index in current implementation.
            return monitors.OrderBy(m => m.Index).FirstOrDefault();
        }

        internal static Dictionary<string, TemplateLayoutSlotDefinition> BuildSlotLookup(
            IReadOnlyList<TemplateLayoutSlotDefinition> slots)
        {
            var map = new Dictionary<string, TemplateLayoutSlotDefinition>(StringComparer.OrdinalIgnoreCase);
            if (slots == null)
            {
                return map;
            }

            foreach (var slot in slots)
            {
                if (slot == null || string.IsNullOrWhiteSpace(slot.Role))
                {
                    continue;
                }

                if (!map.ContainsKey(slot.Role))
                {
                    map[slot.Role] = slot;
                }
            }

            return map;
        }

        internal static LayoutRect ComputeRect(TemplateLayoutSlotDefinition slot, DisplayRect bounds)
        {
            if (slot == null)
            {
                return new LayoutRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            }

            var x = bounds.Left + RoundAwayFromZero(slot.X * bounds.Width);
            var y = bounds.Top + RoundAwayFromZero(slot.Y * bounds.Height);
            var width = Math.Max(slot.MinWidth ?? 480, RoundAwayFromZero(slot.Width * bounds.Width));
            var height = Math.Max(slot.MinHeight ?? 320, RoundAwayFromZero(slot.Height * bounds.Height));

            if (x + width > bounds.Right)
            {
                x = bounds.Right - width;
            }

            if (y + height > bounds.Bottom)
            {
                y = bounds.Bottom - height;
            }

            if (x < bounds.Left)
            {
                x = bounds.Left;
            }

            if (y < bounds.Top)
            {
                y = bounds.Top;
            }

            width = Math.Min(width, bounds.Width);
            height = Math.Min(height, bounds.Height);
            return new LayoutRect(x, y, width, height);
        }

        internal static LayoutRect ResolveOverlap(
            LayoutRect candidate,
            IReadOnlyList<LayoutRect> occupied,
            DisplayRect bounds)
        {
            if (occupied == null || occupied.Count == 0)
            {
                return candidate;
            }

            if (!IntersectsAny(candidate, occupied))
            {
                return candidate;
            }

            var offsets = new[]
            {
                (dx: 8, dy: 0),
                (dx: 0, dy: 8),
                (dx: 8, dy: 8),
                (dx: 16, dy: 0),
                (dx: 0, dy: 16),
                (dx: 16, dy: 16),
            };

            foreach (var (dx, dy) in offsets)
            {
                var moved = ClampToBounds(new LayoutRect(candidate.X + dx, candidate.Y + dy, candidate.Width, candidate.Height), bounds);
                if (!IntersectsAny(moved, occupied))
                {
                    return moved;
                }
            }

            // Try deterministic side placement relative to overlapping rectangles.
            var rightAnchor = bounds.Left;
            foreach (var rect in occupied)
            {
                if (candidate.Y < rect.Bottom && candidate.Bottom > rect.Y)
                {
                    rightAnchor = Math.Max(rightAnchor, rect.Right);
                }
            }

            var rightPlaced = ClampToBounds(new LayoutRect(rightAnchor, candidate.Y, candidate.Width, candidate.Height), bounds);
            if (!IntersectsAny(rightPlaced, occupied))
            {
                return rightPlaced;
            }

            var leftAnchor = bounds.Right;
            foreach (var rect in occupied)
            {
                if (candidate.Y < rect.Bottom && candidate.Bottom > rect.Y)
                {
                    leftAnchor = Math.Min(leftAnchor, rect.X);
                }
            }

            var leftPlaced = ClampToBounds(new LayoutRect(leftAnchor - candidate.Width, candidate.Y, candidate.Width, candidate.Height), bounds);
            if (!IntersectsAny(leftPlaced, occupied))
            {
                return leftPlaced;
            }

            // Last resort: keep top-left and shrink conservatively until non-overlap or minimum bounds.
            var width = candidate.Width;
            var height = candidate.Height;
            while (width > 320 && height > 240)
            {
                width -= 16;
                height -= 16;
                var shrunkX = rightAnchor + width <= bounds.Right ? rightAnchor : candidate.X;
                var shrunk = ClampToBounds(new LayoutRect(shrunkX, candidate.Y, width, height), bounds);
                if (!IntersectsAny(shrunk, occupied))
                {
                    return shrunk;
                }
            }

            return ClampToBounds(candidate, bounds);
        }

        internal static List<TemplateLayoutSlotDefinition> BuildSlots(string strategy, IReadOnlyList<string> roles)
        {
            var normalized = roles?.Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
            if (normalized.Count == 0)
            {
                normalized.Add("app");
            }

            if (string.Equals(strategy, "vertical-equal", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRows(normalized);
            }

            if (string.Equals(strategy, "main-left-70", StringComparison.OrdinalIgnoreCase)
                || string.Equals(strategy, "main-secondary", StringComparison.OrdinalIgnoreCase))
            {
                return BuildMainSecondary(normalized, mainOnLeft: true);
            }

            if (string.Equals(strategy, "main-right-70", StringComparison.OrdinalIgnoreCase))
            {
                return BuildMainSecondary(normalized, mainOnLeft: false);
            }

            if (string.Equals(strategy, "grid-2x2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(strategy, "grid", StringComparison.OrdinalIgnoreCase))
            {
                return BuildGrid(normalized);
            }

            return BuildColumns(normalized);
        }

        private static List<TemplateLayoutSlotDefinition> BuildColumns(IReadOnlyList<string> roles)
        {
            var slots = new List<TemplateLayoutSlotDefinition>(roles.Count);
            for (var i = 0; i < roles.Count; i++)
            {
                var x = i / (double)roles.Count;
                var w = i == roles.Count - 1 ? 1.0 - x : 1.0 / roles.Count;
                slots.Add(new TemplateLayoutSlotDefinition { Role = roles[i], X = x, Y = 0, Width = w, Height = 1 });
            }

            return slots;
        }

        private static List<TemplateLayoutSlotDefinition> BuildRows(IReadOnlyList<string> roles)
        {
            var slots = new List<TemplateLayoutSlotDefinition>(roles.Count);
            for (var i = 0; i < roles.Count; i++)
            {
                var y = i / (double)roles.Count;
                var h = i == roles.Count - 1 ? 1.0 - y : 1.0 / roles.Count;
                slots.Add(new TemplateLayoutSlotDefinition { Role = roles[i], X = 0, Y = y, Width = 1, Height = h });
            }

            return slots;
        }

        private static List<TemplateLayoutSlotDefinition> BuildMainSecondary(IReadOnlyList<string> roles, bool mainOnLeft)
        {
            if (roles.Count == 1)
            {
                return new List<TemplateLayoutSlotDefinition> { new() { Role = roles[0], X = 0, Y = 0, Width = 1, Height = 1 } };
            }

            var slots = new List<TemplateLayoutSlotDefinition> { new() { Role = roles[0], X = mainOnLeft ? 0 : 0.3, Y = 0, Width = 0.7, Height = 1 } };
            var rest = roles.Skip(1).ToList();
            for (var i = 0; i < rest.Count; i++)
            {
                var y = i / (double)rest.Count;
                var h = i == rest.Count - 1 ? 1.0 - y : 1.0 / rest.Count;
                slots.Add(new TemplateLayoutSlotDefinition { Role = rest[i], X = mainOnLeft ? 0.7 : 0, Y = y, Width = 0.3, Height = h });
            }

            return slots;
        }

        private static List<TemplateLayoutSlotDefinition> BuildGrid(IReadOnlyList<string> roles)
        {
            var count = roles.Count;
            var cols = (int)Math.Ceiling(Math.Sqrt(count));
            var rows = (int)Math.Ceiling(count / (double)cols);
            var slots = new List<TemplateLayoutSlotDefinition>(count);
            for (var i = 0; i < count; i++)
            {
                var row = i / cols;
                var col = i % cols;
                var x = col / (double)cols;
                var y = row / (double)rows;
                var w = col == cols - 1 ? 1.0 - x : 1.0 / cols;
                var h = row == rows - 1 ? 1.0 - y : 1.0 / rows;
                slots.Add(new TemplateLayoutSlotDefinition { Role = roles[i], X = x, Y = y, Width = w, Height = h });
            }

            return slots;
        }

        private static int RoundAwayFromZero(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static bool IntersectsAny(LayoutRect candidate, IReadOnlyList<LayoutRect> occupied)
        {
            foreach (var rect in occupied)
            {
                if (Intersects(candidate, rect))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Intersects(LayoutRect a, LayoutRect b)
        {
            return a.X < b.Right
                && a.Right > b.X
                && a.Y < b.Bottom
                && a.Bottom > b.Y;
        }

        private static LayoutRect ClampToBounds(LayoutRect rect, DisplayRect bounds)
        {
            var x = rect.X;
            var y = rect.Y;
            var w = Math.Min(rect.Width, bounds.Width);
            var h = Math.Min(rect.Height, bounds.Height);

            if (x + w > bounds.Right)
            {
                x = bounds.Right - w;
            }

            if (y + h > bounds.Bottom)
            {
                y = bounds.Bottom - h;
            }

            if (x < bounds.Left)
            {
                x = bounds.Left;
            }

            if (y < bounds.Top)
            {
                y = bounds.Top;
            }

            return new LayoutRect(x, y, w, h);
        }
    }
}
