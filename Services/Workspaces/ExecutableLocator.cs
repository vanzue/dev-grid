// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TopToolbar.Services.Workspaces
{
    internal static class ExecutableLocator
    {
        internal readonly record struct Resolution(
            string Id,
            string Requested,
            string Resolved,
            bool Exists,
            string SuggestedRole,
            string DefaultWorkingDirectory,
            string DefaultArgs,
            string Description);

        private readonly record struct KnownApp(
            string Id,
            string SuggestedRole,
            string Description,
            string DefaultWorkingDirectory,
            string DefaultArgs,
            string[] Candidates);

        private static readonly KnownApp[] KnownApps =
        {
            new(
                Id: "copilot",
                SuggestedRole: "terminal",
                Description: "GitHub Copilot CLI agent command.",
                DefaultWorkingDirectory: "{repo}",
                DefaultArgs: string.Empty,
                Candidates: new[] { "copilot", "copilot.exe", "github-copilot.exe" }),
            new(
                Id: "terminal",
                SuggestedRole: "terminal",
                Description: "Windows Terminal.",
                DefaultWorkingDirectory: "{repo}",
                DefaultArgs: string.Empty,
                Candidates: new[] { "wt.exe", "wt" }),
            new(
                Id: "vscode",
                SuggestedRole: "editor",
                Description: "Visual Studio Code.",
                DefaultWorkingDirectory: "{repo}",
                DefaultArgs: "--new-window {repo}",
                Candidates: new[] { "code.exe", "code" }),
            new(
                Id: "visual-studio",
                SuggestedRole: "ide",
                Description: "Visual Studio IDE.",
                DefaultWorkingDirectory: "{repo}",
                DefaultArgs: string.Empty,
                Candidates: new[] { "devenv.exe", "devenv" }),
            new(
                Id: "explorer",
                SuggestedRole: "logs",
                Description: "Windows File Explorer.",
                DefaultWorkingDirectory: "{repo}\\logs",
                DefaultArgs: "/n, \"{repo}\\logs\"",
                Candidates: new[] { "explorer.exe", "explorer" }),
            new(
                Id: "edge",
                SuggestedRole: "browser",
                Description: "Microsoft Edge.",
                DefaultWorkingDirectory: string.Empty,
                DefaultArgs: string.Empty,
                Candidates: new[] { "msedge.exe", "msedge" }),
        };

        internal static IReadOnlyList<Resolution> DiscoverKnown(IReadOnlyList<string> requestedIds = null)
        {
            var requestedSet = requestedIds == null || requestedIds.Count == 0
                ? null
                : new HashSet<string>(
                    requestedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
                    StringComparer.OrdinalIgnoreCase);

            var results = new List<Resolution>();
            foreach (var known in KnownApps)
            {
                if (requestedSet != null && !requestedSet.Contains(known.Id))
                {
                    continue;
                }

                results.Add(ResolveKnown(known));
            }

            return results;
        }

        internal static Resolution Resolve(string appOrExecutable)
        {
            if (string.IsNullOrWhiteSpace(appOrExecutable))
            {
                return new Resolution(
                    Id: string.Empty,
                    Requested: string.Empty,
                    Resolved: string.Empty,
                    Exists: false,
                    SuggestedRole: "app",
                    DefaultWorkingDirectory: string.Empty,
                    DefaultArgs: string.Empty,
                    Description: "Empty executable value.");
            }

            var normalized = appOrExecutable.Trim();
            var known = KnownApps.FirstOrDefault(entry =>
                string.Equals(entry.Id, normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(known.Id))
            {
                return ResolveKnown(known);
            }

            var resolved = ResolveExecutable(normalized);
            var processName = InferProcessName(resolved);
            return new Resolution(
                Id: processName,
                Requested: normalized,
                Resolved: resolved,
                Exists: IsLaunchable(resolved),
                SuggestedRole: "app",
                DefaultWorkingDirectory: string.Empty,
                DefaultArgs: string.Empty,
                Description: "Custom executable.");
        }

        private static Resolution ResolveKnown(KnownApp known)
        {
            foreach (var candidate in known.Candidates)
            {
                var resolved = ResolveExecutable(candidate);
                if (IsLaunchable(resolved))
                {
                    return new Resolution(
                        Id: known.Id,
                        Requested: candidate,
                        Resolved: resolved,
                        Exists: true,
                        SuggestedRole: known.SuggestedRole,
                        DefaultWorkingDirectory: known.DefaultWorkingDirectory,
                        DefaultArgs: known.DefaultArgs,
                        Description: known.Description);
                }
            }

            var fallback = known.Candidates.Length == 0 ? known.Id : known.Candidates[0];
            return new Resolution(
                Id: known.Id,
                Requested: fallback,
                Resolved: fallback,
                Exists: false,
                SuggestedRole: known.SuggestedRole,
                DefaultWorkingDirectory: known.DefaultWorkingDirectory,
                DefaultArgs: known.DefaultArgs,
                Description: known.Description);
        }

        private static string ResolveExecutable(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return string.Empty;
            }

            var resolvedByLauncher = AppLauncher.ResolveTemplateExecutable(executable);
            if (IsLaunchable(resolvedByLauncher))
            {
                return resolvedByLauncher;
            }

            var resolvedByWhere = ResolveWithWhere(executable);
            if (IsLaunchable(resolvedByWhere))
            {
                return resolvedByWhere;
            }

            if (!string.Equals(resolvedByLauncher, executable, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(resolvedByLauncher))
            {
                return resolvedByLauncher;
            }

            return executable.Trim();
        }

        private static bool IsLaunchable(string resolved)
        {
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return false;
            }

            if (resolved.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                return File.Exists(resolved);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveWithWhere(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return string.Empty;
            }

            try
            {
                var command = executable.Trim().Trim('"');
                if (command.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                {
                    return string.Empty;
                }

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = command,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                if (string.IsNullOrWhiteSpace(output))
                {
                    return string.Empty;
                }

                var path = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string InferProcessName(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return string.Empty;
            }

            try
            {
                var name = Path.GetFileNameWithoutExtension(executable.Trim().Trim('"'));
                return string.IsNullOrWhiteSpace(name)
                    ? string.Empty
                    : name.Trim().ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
