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
