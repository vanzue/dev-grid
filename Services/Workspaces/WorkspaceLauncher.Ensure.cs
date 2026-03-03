// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal sealed partial class WorkspaceLauncher
    {
        /// <summary>
        /// Result of ensuring an app is alive
        /// </summary>
        private readonly struct EnsureAppResult
        {
            public static EnsureAppResult Failed(ApplicationDefinition app) => new(false, app, IntPtr.Zero, false);

            public EnsureAppResult(bool success, ApplicationDefinition app, IntPtr handle, bool launchedNew)
            {
                Success = success;
                App = app;
                Handle = handle;
                LaunchedNew = launchedNew;
            }

            public bool Success { get; }
            public ApplicationDefinition App { get; }
            public IntPtr Handle { get; }
            public bool LaunchedNew { get; }
        }

        /// <summary>
        /// Phase 1 Pass 1: Try to assign an existing window to the app (no launching)
        /// </summary>
        private async Task<EnsureAppResult> TryAssignExistingWindowAsync(
            ApplicationDefinition app,
            string workspaceId,
            System.Collections.Generic.IReadOnlyList<WindowInfo> snapshot,
            WindowSnapshotIndex snapshotIndex,
            CancellationToken cancellationToken
        )
        {
            if (app == null)
            {
                return EnsureAppResult.Failed(app);
            }

            await Task.Yield();

            var appLabel = DescribeApp(app);
            var sw = Stopwatch.StartNew();

            try
            {
                LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - begin");

                // Step 1: Check if we already have a managed window bound to this app
                var boundHandle = _managedWindows.GetBoundWindow(app.Id);
                if (boundHandle != IntPtr.Zero)
                {
                    // Verify the window still exists AND matches the app (process must match)
                    if (!NativeWindowHelper.TryCreateWindowInfo(boundHandle, out var windowInfo))
                    {
                        _managedWindows.UnbindWindow(boundHandle);
                        LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - cached window handle={boundHandle} missing, cleared");
                    }
                    else
                    {
                        var boundScore = WorkspaceWindowMatcher.GetMatchScore(windowInfo, app);
                        if (boundScore <= 0)
                        {
                            _managedWindows.UnbindWindow(boundHandle);
                            LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - cached window handle={boundHandle} no longer matches, cleared");
                        }
                        else
                        {
                            if (!EnsureWindowOnCurrentDesktop(boundHandle, appLabel, "TryAssignExisting-cached"))
                            {
                                _managedWindows.UnbindApp(app.Id);
                                LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - cached window handle={boundHandle} unavailable on current desktop, unbound");
                            }
                            else
                            {
                                sw.Stop();
                                LogPerf(
                                    $"WorkspaceRuntime: [{appLabel}] TryAssignExisting - found cached window " +
                                    $"handle={boundHandle} score={boundScore} in {sw.ElapsedMilliseconds} ms");
                                return new EnsureAppResult(true, app, boundHandle, false);
                            }
                        }
                    }
                }

                // Step 2: Try to find an unmanaged window that matches
                var candidateSource = snapshotIndex?.GetCandidates(app);
                if ((candidateSource == null || candidateSource.Count == 0)
                    && snapshot != null
                    && snapshot.Count > 0)
                {
                    candidateSource = snapshot;
                }

                if (candidateSource == null || candidateSource.Count == 0)
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - window snapshot empty in {sw.ElapsedMilliseconds} ms");
                    return EnsureAppResult.Failed(app);
                }

                var loggedBoundCandidate = false;
                var candidates = new System.Collections.Generic.List<(WindowInfo Window, int Score, long Distance, long Area)>();

                foreach (var window in candidateSource)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (window == null || window.Handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (NativeWindowHelper.IsWindowCloaked(window.Handle)
                        && !app.Minimized)
                    {
                        continue;
                    }

                    var score = WorkspaceWindowMatcher.GetMatchScore(window, app);
                    if (score <= 0)
                    {
                        continue;
                    }

                    var boundAppId = _managedWindows.GetBoundAppId(window.Handle);
                    if (boundAppId != null)
                    {
                        if (!loggedBoundCandidate)
                        {
                            loggedBoundCandidate = true;
                            var boundWorkspaceId = _managedWindows.GetWorkspaceIdForApp(boundAppId) ?? "<unknown>";
                            LogPerf(
                                $"WorkspaceRuntime: [{appLabel}] TryAssignExisting - candidate window handle={window.Handle} " +
                                $"already bound to appId={boundAppId} workspaceId={boundWorkspaceId} score={score}");
                        }

                        continue;
                    }

                    if (!EnsureWindowOnCurrentDesktop(window.Handle, appLabel, "TryAssignExisting-candidate"))
                    {
                        LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - candidate window handle={window.Handle} unavailable on current desktop");
                        continue;
                    }

                    candidates.Add((window, score, GetPlacementDistanceSq(window, app), GetWindowArea(window.Bounds)));
                }

                if (candidates.Count > 0)
                {
                    candidates.Sort((left, right) =>
                    {
                        var scoreCompare = right.Score.CompareTo(left.Score);
                        if (scoreCompare != 0)
                        {
                            return scoreCompare;
                        }

                        var distanceCompare = left.Distance.CompareTo(right.Distance);
                        if (distanceCompare != 0)
                        {
                            return distanceCompare;
                        }

                        var areaCompare = right.Area.CompareTo(left.Area);
                        if (areaCompare != 0)
                        {
                            return areaCompare;
                        }

                        return right.Window.Handle.ToInt64().CompareTo(left.Window.Handle.ToInt64());
                    });

                    var best = candidates[0];
                    if (WorkspaceWindowMatcher.IsTitleOnlyMatch(best.Window, app))
                    {
                        var ambiguousCount = 1;
                        var unresolved = false;
                        for (var i = 1; i < candidates.Count; i++)
                        {
                            if (candidates[i].Score != best.Score)
                            {
                                break;
                            }

                            ambiguousCount++;
                            if (candidates[i].Distance == best.Distance)
                            {
                                unresolved = true;
                            }
                        }

                        if (ambiguousCount > 1)
                        {
                            AppLogger.LogWarning(
                                $"WorkspaceRuntime: [{appLabel}] TryAssignExisting - ambiguous title-only match ({ambiguousCount} candidates, title='{app.Title}')");
                        }

                        if (unresolved)
                        {
                            sw.Stop();
                            LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - skipped unresolved title-only match in {sw.ElapsedMilliseconds} ms");
                            return EnsureAppResult.Failed(app);
                        }
                    }

                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var candidate = candidates[i];
                        if (_managedWindows.TryBindWindow(workspaceId, app.Id, candidate.Window.Handle))
                        {
                            sw.Stop();
                            LogPerf(
                                $"WorkspaceRuntime: [{appLabel}] TryAssignExisting - claimed existing window " +
                                $"handle={candidate.Window.Handle} score={candidate.Score} in {sw.ElapsedMilliseconds} ms");
                            return new EnsureAppResult(true, app, candidate.Window.Handle, false);
                        }
                    }
                }

                sw.Stop();
                LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - no existing window found in {sw.ElapsedMilliseconds} ms");
                return EnsureAppResult.Failed(app);
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.LogWarning($"WorkspaceRuntime: [{appLabel}] TryAssignExisting failed - {ex.Message}");
                return EnsureAppResult.Failed(app);
            }
        }

        /// <summary>
        /// Phase 1 Pass 2: Launch a new window for the app
        /// </summary>
        private async Task<EnsureAppResult> LaunchNewWindowAsync(
            ApplicationDefinition app,
            string workspaceId,
            CancellationToken cancellationToken
        )
        {
            if (app == null)
            {
                return EnsureAppResult.Failed(app);
            }

            var appLabel = DescribeApp(app);
            var sw = Stopwatch.StartNew();

            try
            {
                LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - begin");

                if (app.Minimized)
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - skipped because app is snapshotted as minimized and no existing window was found");
                    return new EnsureAppResult(true, app, IntPtr.Zero, false);
                }

                if (HasMatchingCurrentDesktopWindowForApp(app))
                {
                    sw.Stop();
                    LogPerf(
                        $"WorkspaceRuntime: [{appLabel}] LaunchNew - skipped because a matching window already exists on current desktop");
                    return EnsureAppResult.Failed(app);
                }

                // ApplicationFrameHost cannot be launched directly
                if (IsApplicationFrameHostPath(app.Path))
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - cannot launch ApplicationFrameHost directly");
                    return EnsureAppResult.Failed(app);
                }

                var excludedHandles = _managedWindows.GetAllBoundWindows();
                var launchResult = await AppLauncher.LaunchAppAsync(
                    app,
                    _windowManager,
                    WindowWaitTimeout,
                    WindowPollInterval,
                    excludedHandles,
                    cancellationToken
                ).ConfigureAwait(false);

                if (!launchResult.Succeeded || launchResult.Windows.Count == 0)
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - launch failed in {sw.ElapsedMilliseconds} ms");
                    return EnsureAppResult.Failed(app);
                }

                var newHandle = await PickLaunchWindowAsync(app, launchResult.Windows, cancellationToken)
                    .ConfigureAwait(false);
                if (newHandle == IntPtr.Zero)
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - no eligible window found after launch in {sw.ElapsedMilliseconds} ms");
                    return EnsureAppResult.Failed(app);
                }

                if (!EnsureWindowOnCurrentDesktop(newHandle, appLabel, "LaunchNew-picked"))
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - picked window is not available on current virtual desktop in {sw.ElapsedMilliseconds} ms");
                    return EnsureAppResult.Failed(app);
                }

                if (_managedWindows.TryBindWindow(workspaceId, app.Id, newHandle))
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - launched and claimed window handle={newHandle} in {sw.ElapsedMilliseconds} ms");
                    return new EnsureAppResult(true, app, newHandle, true);
                }
                else
                {
                    sw.Stop();
                    var boundAppId = _managedWindows.GetBoundAppId(newHandle) ?? "<unknown>";
                    var boundWorkspaceId = _managedWindows.GetWorkspaceIdForApp(boundAppId) ?? "<unknown>";
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - launched but window already claimed (handle={newHandle} appId={boundAppId} workspaceId={boundWorkspaceId}) in {sw.ElapsedMilliseconds} ms");
                    return EnsureAppResult.Failed(app);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.LogWarning($"WorkspaceRuntime: [{appLabel}] LaunchNew failed - {ex.Message}");
                return EnsureAppResult.Failed(app);
            }
        }

        private async Task<IntPtr> PickLaunchWindowAsync(
            ApplicationDefinition app,
            System.Collections.Generic.IReadOnlyList<WindowInfo> candidates,
            CancellationToken cancellationToken)
        {
            var best = SelectBestLaunchCandidate(candidates, app);
            var bestHandle = best?.Handle ?? IntPtr.Zero;
            var bestScore = best != null ? WorkspaceWindowMatcher.GetMatchScore(best, app) : -1;
            var bestArea = best != null ? GetWindowArea(best.Bounds) : -1L;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < LaunchWindowSettleTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(LaunchWindowSettlePollInterval, cancellationToken).ConfigureAwait(false);

                var snapshot = _windowManager.GetSnapshot();
                var candidate = SelectBestLaunchCandidate(snapshot, app);
                if (candidate == null)
                {
                    continue;
                }

                var candidateScore = WorkspaceWindowMatcher.GetMatchScore(candidate, app);
                var candidateArea = GetWindowArea(candidate.Bounds);
                var bestAlive = bestHandle != IntPtr.Zero
                    && NativeWindowHelper.TryGetWindowBounds(bestHandle, out _);

                if (!bestAlive
                    || candidateScore > bestScore
                    || (candidateScore == bestScore && candidateArea > bestArea))
                {
                    best = candidate;
                    bestHandle = candidate.Handle;
                    bestScore = candidateScore;
                    bestArea = candidateArea;
                }
            }

            return bestHandle;
        }

        private WindowInfo SelectBestLaunchCandidate(
            System.Collections.Generic.IEnumerable<WindowInfo> windows,
            ApplicationDefinition app)
        {
            if (windows == null)
            {
                return null;
            }

            WindowInfo best = null;
            var bestScore = -1;
            long bestArea = -1;

            foreach (var window in windows)
            {
                if (!IsEligibleLaunchWindow(window, app?.Minimized ?? false))
                {
                    continue;
                }

                var score = WorkspaceWindowMatcher.GetMatchScore(window, app);
                if (score <= 0)
                {
                    continue;
                }

                var boundAppId = _managedWindows.GetBoundAppId(window.Handle);
                if (boundAppId != null
                    && !string.Equals(boundAppId, app?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var area = GetWindowArea(window.Bounds);
                var onCurrentDesktop = IsWindowOnCurrentDesktop(window.Handle);
                if (!onCurrentDesktop)
                {
                    continue;
                }

                if (score > bestScore
                    || (score == bestScore && area > bestArea))
                {
                    best = window;
                    bestScore = score;
                    bestArea = area;
                }
            }

            return best;
        }

        private static bool IsEligibleLaunchWindow(WindowInfo window, bool allowCloaked)
        {
            if (window == null || window.Handle == IntPtr.Zero)
            {
                return false;
            }

            if (window.Bounds.IsEmpty)
            {
                return false;
            }

            if (NativeWindowHelper.HasToolWindowStyle(window.Handle))
            {
                return false;
            }

            if (!allowCloaked && NativeWindowHelper.IsWindowCloaked(window.Handle))
            {
                return false;
            }

            return true;
        }

        private static long GetWindowArea(WindowBounds bounds)
        {
            if (bounds.IsEmpty)
            {
                return 0;
            }

            return (long)bounds.Width * bounds.Height;
        }

        private static long GetPlacementDistanceSq(WindowInfo window, ApplicationDefinition app)
        {
            if (window == null || app == null)
            {
                return long.MaxValue;
            }

            if (app.Position == null || app.Position.IsEmpty || window.Bounds.IsEmpty)
            {
                if (app.MonitorIndex > 0 && window.MonitorIndex > 0)
                {
                    return app.MonitorIndex == window.MonitorIndex ? 0 : 1_000_000_000L;
                }

                return long.MaxValue;
            }

            var appCenterX = (long)app.Position.X + (app.Position.Width / 2L);
            var appCenterY = (long)app.Position.Y + (app.Position.Height / 2L);
            var windowCenterX = (long)window.Bounds.Left + (window.Bounds.Width / 2L);
            var windowCenterY = (long)window.Bounds.Top + (window.Bounds.Height / 2L);

            var dx = appCenterX - windowCenterX;
            var dy = appCenterY - windowCenterY;
            return (dx * dx) + (dy * dy);
        }

        private bool HasMatchingCurrentDesktopWindowForApp(ApplicationDefinition app)
        {
            try
            {
                if (app == null)
                {
                    return false;
                }

                var snapshot = _windowManager.GetSnapshot();
                if (snapshot == null || snapshot.Count == 0)
                {
                    return false;
                }

                for (var i = 0; i < snapshot.Count; i++)
                {
                    var window = snapshot[i];
                    if (window == null || window.Handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (WorkspaceWindowMatcher.GetMatchScore(window, app) > 0
                        && IsWindowOnCurrentDesktop(window.Handle))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private WindowSnapshotIndex BuildWindowSnapshotIndex(
            System.Collections.Generic.IReadOnlyList<WindowInfo> snapshot)
        {
            return WindowSnapshotIndex.Build(snapshot);
        }

        private sealed class WindowSnapshotIndex
        {
            private readonly System.Collections.Generic.IReadOnlyList<WindowInfo> _all;
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byAumid =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byPackageFullName =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byPackageFamily =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byProcessPath =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byProcessFileName =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byProcessName =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> _byTitle =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Generic.List<WindowInfo> _browserWindows = new();

            private WindowSnapshotIndex(System.Collections.Generic.IReadOnlyList<WindowInfo> all)
            {
                _all = all ?? Array.Empty<WindowInfo>();
            }

            public static WindowSnapshotIndex Build(System.Collections.Generic.IReadOnlyList<WindowInfo> snapshot)
            {
                var index = new WindowSnapshotIndex(snapshot);
                if (snapshot == null || snapshot.Count == 0)
                {
                    return index;
                }

                for (var i = 0; i < snapshot.Count; i++)
                {
                    index.Add(snapshot[i]);
                }

                return index;
            }

            public System.Collections.Generic.IReadOnlyList<WindowInfo> GetCandidates(ApplicationDefinition app)
            {
                if (app == null || _all.Count == 0)
                {
                    return Array.Empty<WindowInfo>();
                }

                var matches = new System.Collections.Generic.List<WindowInfo>();
                var seen = new System.Collections.Generic.HashSet<IntPtr>();
                var addedAny = false;

                addedAny |= AddFromDictionary(_byAumid, NormalizeToken(app.AppUserModelId), matches, seen);
                addedAny |= AddFromDictionary(_byPackageFullName, NormalizeToken(app.PackageFullName), matches, seen);
                addedAny |= AddFromDictionary(_byPackageFamily, GetPackageFamilyName(app.PackageFullName), matches, seen);

                if (!WorkspaceLauncher.IsApplicationFrameHostPath(app.Path))
                {
                    addedAny |= AddFromDictionary(_byProcessPath, NormalizePath(app.Path), matches, seen);
                    addedAny |= AddFromDictionary(_byProcessFileName, NormalizeFileName(app.Path), matches, seen);
                    addedAny |= AddFromDictionary(_byProcessName, NormalizeProcessName(app.Name), matches, seen);
                }

                addedAny |= AddFromDictionary(_byTitle, NormalizeToken(app.Title), matches, seen);

                if (!string.IsNullOrWhiteSpace(app.PwaAppId))
                {
                    var pwa = app.PwaAppId.Trim();
                    for (var i = 0; i < _browserWindows.Count; i++)
                    {
                        var window = _browserWindows[i];
                        if (window == null || string.IsNullOrWhiteSpace(window.AppUserModelId))
                        {
                            continue;
                        }

                        if (!window.AppUserModelId.Contains(pwa, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (seen.Add(window.Handle))
                        {
                            matches.Add(window);
                            addedAny = true;
                        }
                    }
                }

                if (!addedAny)
                {
                    return _all;
                }

                return matches.Count > 0 ? matches : _all;
            }

            private void Add(WindowInfo window)
            {
                if (window == null || window.Handle == IntPtr.Zero)
                {
                    return;
                }

                AddToDictionary(_byAumid, NormalizeToken(window.AppUserModelId), window);
                AddToDictionary(_byPackageFullName, NormalizeToken(window.PackageFullName), window);
                AddToDictionary(_byPackageFamily, GetPackageFamilyName(window.PackageFullName), window);
                AddToDictionary(_byProcessPath, NormalizePath(window.ProcessPath), window);
                AddToDictionary(_byProcessFileName, NormalizeFileName(window.ProcessPath), window);
                AddToDictionary(_byProcessName, NormalizeProcessName(window.ProcessName), window);
                AddToDictionary(_byTitle, NormalizeToken(window.Title), window);

                if (IsBrowserProcess(window))
                {
                    _browserWindows.Add(window);
                }
            }

            private static bool AddFromDictionary(
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> map,
                string key,
                System.Collections.Generic.List<WindowInfo> output,
                System.Collections.Generic.HashSet<IntPtr> seen)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                if (!map.TryGetValue(key, out var values) || values == null || values.Count == 0)
                {
                    return false;
                }

                var added = false;
                for (var i = 0; i < values.Count; i++)
                {
                    var window = values[i];
                    if (window == null || !seen.Add(window.Handle))
                    {
                        continue;
                    }

                    output.Add(window);
                    added = true;
                }

                return added;
            }

            private static void AddToDictionary(
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<WindowInfo>> map,
                string key,
                WindowInfo window)
            {
                if (string.IsNullOrWhiteSpace(key) || window == null)
                {
                    return;
                }

                if (!map.TryGetValue(key, out var values))
                {
                    values = new System.Collections.Generic.List<WindowInfo>();
                    map[key] = values;
                }

                values.Add(window);
            }

            private static bool IsBrowserProcess(WindowInfo window)
            {
                var processName = NormalizeProcessName(window?.ProcessName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    processName = NormalizeProcessName(window?.ProcessFileName);
                }

                return string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizeToken(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            }

            private static string NormalizeFileName(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return string.Empty;
                }

                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(path).Trim('"');
                    return Path.GetFileName(expanded);
                }
                catch
                {
                    return Path.GetFileName(path.Trim('"'));
                }
            }

            private static string NormalizeProcessName(string processName)
            {
                if (string.IsNullOrWhiteSpace(processName))
                {
                    return string.Empty;
                }

                var normalized = processName.Trim();
                if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - 4);
                }

                return normalized;
            }

            private static string NormalizePath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return string.Empty;
                }

                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(path).Trim('"');
                    if (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    {
                        return expanded;
                    }

                    return Path.GetFullPath(expanded);
                }
                catch
                {
                    return path.Trim('"');
                }
            }

            private static string GetPackageFamilyName(string packageFullName)
            {
                if (string.IsNullOrWhiteSpace(packageFullName))
                {
                    return string.Empty;
                }

                var parts = packageFullName.Split('_');
                if (parts.Length < 2)
                {
                    return string.Empty;
                }

                var name = parts[0];
                var publisher = parts[^1];
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher))
                {
                    return string.Empty;
                }

                return $"{name}_{publisher}";
            }
        }

    }
}
