// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal sealed partial class WorkspaceLauncher
    {
        /// <summary>
        /// Phase 2: Resize a window to its target position
        /// </summary>
        private async Task<bool> ResizeWindowWithTimeoutAsync(
            IntPtr handle,
            ApplicationDefinition app,
            WindowPlacement targetPosition,
            bool launchedNew,
            IReadOnlyDictionary<IntPtr, bool> workspaceWindowMinimizeStates,
            CancellationToken cancellationToken)
        {
            const int resizeTimeoutSeconds = 15;
            var appLabel = DescribeApp(app);
            try
            {
                return await ResizeWindowAsync(
                        handle,
                        app,
                        targetPosition,
                        launchedNew,
                        workspaceWindowMinimizeStates,
                        cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(resizeTimeoutSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLogger.LogWarning(
                    $"WorkspaceRuntime: [{appLabel}] Resize timed out after {resizeTimeoutSeconds}s; continuing with partial arrange.");
                return false;
            }
        }

        /// <summary>
        /// Phase 2: Resize a window to its target position
        /// </summary>
        private async Task<bool> ResizeWindowAsync(
            IntPtr handle,
            ApplicationDefinition app,
            WindowPlacement targetPosition,
            bool launchedNew,
            IReadOnlyDictionary<IntPtr, bool> workspaceWindowMinimizeStates,
            CancellationToken cancellationToken
        )
        {
            if (handle == IntPtr.Zero || app == null)
            {
                return false;
            }

            // Yield immediately to ensure parallel execution
            await Task.Yield();

            var appLabel = DescribeApp(app);
            var sw = Stopwatch.StartNew();

            try
            {
                if (!EnsureWindowOnCurrentDesktop(
                    handle,
                    appLabel,
                    "Resize",
                    allowUnknownDesktopState: true))
                {
                    return false;
                }

                if (NativeWindowHelper.IsWindowCloaked(handle))
                {
                    LogPerf($"WorkspaceRuntime: [{appLabel}] Resize - window remains cloaked after desktop check; continuing placement attempt");
                }

                LogPerf($"WorkspaceRuntime: [{appLabel}] Resize - begin: minimized={app.Minimized}, maximized={app.Maximized}, position=({app.Position?.X},{app.Position?.Y},{app.Position?.Width},{app.Position?.Height})");

                // For minimized windows in switch mode, speed matters more than exact geometry.
                // Apply minimize directly and skip costly placement verification loops.
                if (app.Minimized)
                {
                    NativeWindowHelper.MinimizeWindow(handle);
                    if (ShouldMinimizeSiblingProcessWindows(app))
                    {
                        MinimizeSiblingProcessWindows(handle, appLabel, workspaceWindowMinimizeStates);
                    }

                    sw.Stop();
                    LogPerf($"WorkspaceRuntime: [{appLabel}] Resize - fast-path minimized in {sw.ElapsedMilliseconds} ms");
                    return true;
                }

                var position = !targetPosition.IsEmpty ? targetPosition : default;

                await NativeWindowHelper.SetWindowPlacementAsync(
                    handle,
                    position,
                    app.Maximized,
                    app.Minimized,
                    launchedNew,
                    cancellationToken
                ).ConfigureAwait(false);

                if (launchedNew && !position.IsEmpty)
                {
                    await MakeSureWindowArrangedAsync(handle, position, app.Maximized, app.Minimized, cancellationToken)
                        .ConfigureAwait(false);
                    await PostSettleWindowAsync(handle, position, app.Maximized, app.Minimized, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (app.Maximized)
                {
                    // Existing windows can ignore a single ShowWindow call (especially multi-window apps).
                    // Verify and retry until expected state is reached or timeout expires.
                    await MakeSureWindowArrangedAsync(handle, position, app.Maximized, app.Minimized, cancellationToken)
                        .ConfigureAwait(false);
                }

                sw.Stop();
                LogPerf($"WorkspaceRuntime: [{appLabel}] Resize - done in {sw.ElapsedMilliseconds} ms");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.LogWarning($"WorkspaceRuntime: [{appLabel}] Resize failed in {sw.ElapsedMilliseconds} ms - {ex.Message}");
                return false;
            }
        }

        private void MinimizeSiblingProcessWindows(
            IntPtr primaryHandle,
            string appLabel,
            IReadOnlyDictionary<IntPtr, bool> workspaceWindowMinimizeStates)
        {
            try
            {
                if (primaryHandle == IntPtr.Zero)
                {
                    return;
                }

                if (workspaceWindowMinimizeStates == null || workspaceWindowMinimizeStates.Count == 0)
                {
                    return;
                }

                if (!NativeWindowHelper.TryCreateWindowInfo(primaryHandle, out var primaryInfo)
                    || primaryInfo == null
                    || primaryInfo.ProcessId == 0)
                {
                    return;
                }

                var siblingHandles = NativeWindowHelper.EnumerateProcessWindows((int)primaryInfo.ProcessId);
                if (siblingHandles == null || siblingHandles.Count == 0)
                {
                    return;
                }

                foreach (var hwnd in siblingHandles)
                {
                    if (hwnd == IntPtr.Zero || hwnd == primaryHandle)
                    {
                        continue;
                    }

                    if (!workspaceWindowMinimizeStates.TryGetValue(hwnd, out var shouldBeMinimized)
                        || !shouldBeMinimized)
                    {
                        continue;
                    }

                    if (!NativeWindowHelper.IsWindowHandleValid(hwnd))
                    {
                        continue;
                    }

                    if (NativeWindowHelper.IsWindowCloaked(hwnd))
                    {
                        continue;
                    }

                    if (!NativeWindowHelper.TryIsWindowVisible(hwnd, out var visible) || !visible)
                    {
                        continue;
                    }

                    if (!NativeWindowHelper.CanMinimizeWindow(hwnd))
                    {
                        continue;
                    }

                    NativeWindowHelper.MinimizeWindow(hwnd);
                    LogPerf($"WorkspaceRuntime: [{appLabel}] Resize - minimized sibling window handle={hwnd}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: [{appLabel}] Resize - minimize sibling windows failed: {ex.Message}");
            }
        }

        private static bool ShouldMinimizeSiblingProcessWindows(ApplicationDefinition app)
        {
            if (app == null)
            {
                return false;
            }

            // Windows Terminal has multiple independent top-level windows in one process.
            // Minimizing "siblings" can hide a different terminal that should remain visible
            // according to the snapshot.
            if (IsExecutable(app.Path, "WindowsTerminal.exe")
                || IsExecutable(app.Name, "WindowsTerminal.exe")
                || IsExecutable(app.Path, "wt.exe")
                || IsExecutable(app.Name, "wt.exe"))
            {
                return false;
            }

            return true;
        }

        private HashSet<uint> GetWorkspaceProcessIds(IReadOnlyCollection<EnsureAppResult> successfulApps)
        {
            var processIds = new HashSet<uint>();
            if (successfulApps == null || successfulApps.Count == 0)
            {
                return processIds;
            }

            foreach (var result in successfulApps)
            {
                if (result.Handle == IntPtr.Zero)
                {
                    continue;
                }

                if (NativeWindowHelper.TryCreateWindowInfo(result.Handle, out var info)
                    && info != null
                    && info.ProcessId != 0)
                {
                    processIds.Add(info.ProcessId);
                }
            }

            return processIds;
        }

        private int MinimizeExtraneousWindows(
            HashSet<IntPtr> workspaceHandles,
            HashSet<uint> workspaceProcessIds,
            CancellationToken cancellationToken)
        {
            var minimizedCount = 0;
            try
            {
                var currentProcessId = (uint)Environment.ProcessId;
                var snapshot = _windowManager.GetSnapshot();
                var phaseSw = Stopwatch.StartNew();

                foreach (var window in snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (phaseSw.Elapsed > MinimizeExtraneousBudget)
                    {
                        AppLogger.LogWarning(
                            $"WorkspaceRuntime: Phase 3 minimize budget reached after {phaseSw.ElapsedMilliseconds} ms; minimized={minimizedCount}, remaining windows skipped.");
                        break;
                    }

                    if (window.ProcessId == currentProcessId)
                    {
                        continue;
                    }

                    if (workspaceHandles != null && workspaceHandles.Contains(window.Handle))
                    {
                        continue;
                    }

                    if (workspaceProcessIds != null && workspaceProcessIds.Contains(window.ProcessId))
                    {
                        continue;
                    }

                    if (NativeWindowHelper.IsWindowCloaked(window.Handle))
                    {
                        continue;
                    }

                    var desktopAvailability = TryGetWindowDesktopAvailabilitySafe(window.Handle);
                    if (desktopAvailability != DesktopAvailability.Current)
                    {
                        if (desktopAvailability == DesktopAvailability.Unknown)
                        {
                            AppLogger.LogWarning(
                                $"WorkspaceRuntime: desktop query unavailable for window handle={window.Handle}; skipping minimize for safety.");
                        }

                        continue;
                    }

                    if (!NativeWindowHelper.CanMinimizeWindow(window.Handle))
                    {
                        continue;
                    }

                    NativeWindowHelper.MinimizeWindow(window.Handle);
                    minimizedCount++;
                    LogPerf(
                        $"WorkspaceRuntime: Phase 3 - minimized extraneous window handle={window.Handle}, processId={window.ProcessId}, title='{window.Title}'");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(
                    $"WorkspaceRuntime: failed to minimize extraneous windows - {ex.Message}"
                );
            }

            return minimizedCount;
        }

        private static DesktopAvailability TryGetWindowDesktopAvailabilitySafe(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return DesktopAvailability.Unknown;
            }

            try
            {
                var task = Task.Run(() =>
                {
                    var queryOk = NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(handle, out var onCurrentDesktop);
                    if (!queryOk)
                    {
                        return DesktopAvailability.Unknown;
                    }

                    return onCurrentDesktop
                        ? DesktopAvailability.Current
                        : DesktopAvailability.Other;
                });

                if (!task.Wait(TimeSpan.FromMilliseconds(200)))
                {
                    AppLogger.LogWarning(
                        $"WorkspaceRuntime: desktop query timeout for window handle={handle}.");
                    return DesktopAvailability.Unknown;
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(
                    $"WorkspaceRuntime: desktop query failed for window handle={handle} - {ex.Message}");
                return DesktopAvailability.Unknown;
            }
        }

        private async Task MakeSureWindowArrangedAsync(
            IntPtr handle,
            WindowPlacement position,
            bool maximize,
            bool minimize,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var stableChecks = 0;

            while (sw.Elapsed < WindowArrangeTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!NativeWindowHelper.TryGetWindowPlacement(
                    handle,
                    out var bounds,
                    out var isMinimized,
                    out var isMaximized))
                {
                    return;
                }

                if (IsExpectedPlacement(bounds, position, isMinimized, isMaximized, minimize, maximize))
                {
                    stableChecks++;
                    if (stableChecks >= WindowArrangeStableChecks)
                    {
                        return;
                    }
                }
                else
                {
                    stableChecks = 0;
                    await NativeWindowHelper.SetWindowPlacementAsync(
                        handle,
                        position,
                        maximize,
                        minimize,
                        waitForInputIdle: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(WindowArrangePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task PostSettleWindowAsync(
            IntPtr handle,
            WindowPlacement position,
            bool maximize,
            bool minimize,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < WindowPostSettleTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!EnsureWindowOnCurrentDesktop(
                    handle,
                    "<post-settle>",
                    "PostSettle",
                    allowUnknownDesktopState: true))
                {
                    return;
                }

                if (!NativeWindowHelper.TryGetWindowPlacement(
                    handle,
                    out var bounds,
                    out var isMinimized,
                    out var isMaximized))
                {
                    return;
                }

                if (!IsExpectedPlacement(bounds, position, isMinimized, isMaximized, minimize, maximize))
                {
                    await NativeWindowHelper.SetWindowPlacementAsync(
                        handle,
                        position,
                        maximize,
                        minimize,
                        waitForInputIdle: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(WindowPostSettlePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsExpectedPlacement(
            WindowBounds bounds,
            WindowPlacement target,
            bool isMinimized,
            bool isMaximized,
            bool expectMinimized,
            bool expectMaximized)
        {
            if (expectMinimized)
            {
                return isMinimized;
            }

            if (expectMaximized)
            {
                return isMaximized;
            }

            if (target.IsEmpty || bounds.IsEmpty)
            {
                return false;
            }

            return Math.Abs(bounds.Left - target.X) <= WindowArrangeTolerancePixels
                && Math.Abs(bounds.Top - target.Y) <= WindowArrangeTolerancePixels
                && Math.Abs(bounds.Width - target.Width) <= WindowArrangeTolerancePixels
                && Math.Abs(bounds.Height - target.Height) <= WindowArrangeTolerancePixels;
        }
    }
}
