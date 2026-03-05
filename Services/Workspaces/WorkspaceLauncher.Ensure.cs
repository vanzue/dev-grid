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
        private static readonly TimeSpan AssignExistingWindowTimeout = TimeSpan.FromSeconds(10);

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
            bool launchNewIfUnbound,
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
                            if (!EnsureWindowOnCurrentDesktop(
                                boundHandle,
                                appLabel,
                                "TryAssignExisting-cached",
                                allowUnknownDesktopState: true))
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

                // Deterministic workspace contract:
                // only restore windows already bound to this workspace app id.
                sw.Stop();
                if (launchNewIfUnbound)
                {
                    LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - no bound window found; launch required in {sw.ElapsedMilliseconds} ms");
                }
                else
                {
                    LogPerf($"WorkspaceRuntime: [{appLabel}] TryAssignExisting - no bound window found in {sw.ElapsedMilliseconds} ms");
                }
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
        /// Phase 1 Pass 1 with timeout guard to prevent individual app assignment hangs.
        /// </summary>
        private async Task<EnsureAppResult> TryAssignExistingWindowWithTimeoutAsync(
            ApplicationDefinition app,
            bool launchNewIfUnbound,
            CancellationToken cancellationToken)
        {
            var appLabel = DescribeApp(app);
            try
            {
                return await TryAssignExistingWindowAsync(
                        app,
                        launchNewIfUnbound,
                        cancellationToken)
                    .WaitAsync(AssignExistingWindowTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLogger.LogWarning(
                    $"WorkspaceRuntime: [{appLabel}] TryAssignExisting timed out after {AssignExistingWindowTimeout.TotalSeconds:0}s; continuing.");
                return EnsureAppResult.Failed(app);
            }
        }

        /// <summary>
        /// Phase 1 Pass 2: Launch a new window for the app
        /// </summary>
        private async Task<EnsureAppResult> LaunchNewWindowAsync(
            ApplicationDefinition app,
            string workspaceId,
            bool launchNewIfUnbound,
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

                if (!launchNewIfUnbound)
                {
                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - skipped because launch-new-if-unbound is disabled");
                    return EnsureAppResult.Failed(app);
                }

                var originalLaunchPreference = app.LaunchNewIfUnbound;
                var launchPreferenceChanged = false;
                if (launchNewIfUnbound && !originalLaunchPreference)
                {
                    app.LaunchNewIfUnbound = true;
                    launchPreferenceChanged = true;
                }

                try
                {
                    var hasBoundHandle = _managedWindows.GetBoundWindow(app.Id) != IntPtr.Zero;
                    if (HasMatchingCurrentDesktopWindowForApp(app))
                    {
                        if (!launchNewIfUnbound || hasBoundHandle)
                        {
                            sw.Stop();
                            LogPerf(
                                $"WorkspaceRuntime: [{appLabel}] LaunchNew - skipped because a matching window already exists on current desktop");
                            return EnsureAppResult.Failed(app);
                        }
                    }

                    // ApplicationFrameHost cannot be launched directly
                    if (IsApplicationFrameHostPath(app.Path))
                    {
                        sw.Stop();
                        LogPerf($"WorkspaceRuntime: [{appLabel}] LaunchNew - cannot launch ApplicationFrameHost directly");
                        return EnsureAppResult.Failed(app);
                    }

                    var excludedHandles = new System.Collections.Generic.HashSet<IntPtr>(_managedWindows.GetAllBoundWindows());
                    if (launchNewIfUnbound && !hasBoundHandle)
                    {
                        var preLaunchSnapshot = _windowManager.GetSnapshot();
                        if (preLaunchSnapshot != null)
                        {
                            for (var i = 0; i < preLaunchSnapshot.Count; i++)
                            {
                                var existingHandle = preLaunchSnapshot[i]?.Handle ?? IntPtr.Zero;
                                if (existingHandle != IntPtr.Zero)
                                {
                                    excludedHandles.Add(existingHandle);
                                }
                            }
                        }
                    }

                    var launchResult = await AppLauncher.LaunchAppAsync(
                        app,
                        _windowManager,
                        GetWindowWaitTimeout(app),
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

                    if (!EnsureWindowOnCurrentDesktop(
                        newHandle,
                        appLabel,
                        "LaunchNew-picked",
                        allowUnknownDesktopState: true))
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
                finally
                {
                    if (launchPreferenceChanged)
                    {
                        app.LaunchNewIfUnbound = originalLaunchPreference;
                    }
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

        private static TimeSpan GetWindowWaitTimeout(ApplicationDefinition app)
        {
            if (IsExecutable(app?.Path, "code.exe")
                || IsExecutable(app?.Name, "code.exe"))
            {
                // VS Code can take longer to materialize a top-level window, especially when
                // opening a new folder window while extensions initialize.
                return TimeSpan.FromSeconds(25);
            }

            return WindowWaitTimeout;
        }

        private static bool IsExecutable(string pathOrName, string expectedExe)
        {
            if (string.IsNullOrWhiteSpace(pathOrName) || string.IsNullOrWhiteSpace(expectedExe))
            {
                return false;
            }

            try
            {
                var fileName = Path.GetFileName(pathOrName.Trim().Trim('"'));
                return string.Equals(fileName, expectedExe, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(pathOrName.Trim().Trim('"'), expectedExe, StringComparison.OrdinalIgnoreCase);
            }
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

    }
}
