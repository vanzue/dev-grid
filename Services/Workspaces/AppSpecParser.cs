// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace TopToolbar.Services.Workspaces
{
    internal static class AppSpecParser
    {
        internal static bool TryParse(string raw, out WorkspaceAppSpec spec, out string error)
        {
            spec = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "empty app spec";
                return false;
            }

            var segments = SplitTopLevel(raw, ',');
            if (segments.Count == 0)
            {
                error = "empty app spec";
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in segments)
            {
                var piece = segment?.Trim();
                if (string.IsNullOrWhiteSpace(piece))
                {
                    continue;
                }

                var separatorIndex = FindTopLevelEquals(piece);
                if (separatorIndex <= 0 || separatorIndex >= piece.Length - 1)
                {
                    error = $"invalid key=value segment '{piece}'";
                    return false;
                }

                var key = piece.Substring(0, separatorIndex).Trim();
                var value = Unquote(piece.Substring(separatorIndex + 1).Trim());
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = $"invalid key in segment '{piece}'";
                    return false;
                }

                if (!IsSupportedKey(key))
                {
                    error = $"unknown key '{key}'";
                    return false;
                }

                if (!values.TryAdd(key, value))
                {
                    error = $"duplicate key '{key}'";
                    return false;
                }
            }

            if (!values.TryGetValue("role", out var role) || string.IsNullOrWhiteSpace(role))
            {
                error = "missing required key 'role'";
                return false;
            }

            if (!values.TryGetValue("exe", out var exe) || string.IsNullOrWhiteSpace(exe))
            {
                error = "missing required key 'exe'";
                return false;
            }

            values.TryGetValue("cwd", out var cwd);
            values.TryGetValue("args", out var args);
            values.TryGetValue("init", out var init);
            values.TryGetValue("monitor", out var monitor);

            spec = new WorkspaceAppSpec(
                role.Trim().ToLowerInvariant(),
                exe.Trim(),
                (cwd ?? string.Empty).Trim(),
                (args ?? string.Empty).Trim(),
                (init ?? string.Empty).Trim(),
                (monitor ?? string.Empty).Trim());
            return true;
        }

        private static bool IsSupportedKey(string key)
        {
            return string.Equals(key, "role", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "cwd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "args", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "init", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "monitor", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> SplitTopLevel(string input, char delimiter)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(input))
            {
                return parts;
            }

            var start = 0;
            var inQuotes = false;
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c == '"' && (i == 0 || input[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == delimiter && !inQuotes)
                {
                    parts.Add(input.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (start <= input.Length)
            {
                parts.Add(input.Substring(start));
            }

            return parts;
        }

        private static int FindTopLevelEquals(string segment)
        {
            var inQuotes = false;
            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                if (c == '"' && (i == 0 || segment[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == '=' && !inQuotes)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }

            return trimmed
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
    }
}
