// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Display;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal sealed partial class WorkspaceLauncher
    {
        private static string DescribeApp(ApplicationDefinition app)
        {
            if (app == null)
            {
                return "<null>";
            }

            var parts = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(app.Name))
            {
                parts.Add(app.Name.Trim());
            }

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                parts.Add(app.Path.Trim());
            }

            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                parts.Add(app.AppUserModelId.Trim());
            }

            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(app.Title))
            {
                parts.Add(app.Title.Trim());
            }

            var identity = string.Join(" | ", parts);
            var id = string.IsNullOrWhiteSpace(app.Id) ? "<no-id>" : app.Id;
            return string.IsNullOrWhiteSpace(identity) ? $"id={id}" : $"{identity} (id={id})";
        }

        private static bool IsApplicationFrameHostPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            return string.Equals(fileName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static void LogPerf(string message)
        {
            try
            {
                AppLogger.LogInfo(message);
                try
                {
                    if (WorkspaceRuntimeConsoleOptions.EnableConsoleTrace)
                    {
                        var ts = DateTime.Now.ToString(
                            "HH:mm:ss.fff",
                            System.Globalization.CultureInfo.InvariantCulture
                        );
                        System.Console.WriteLine("[" + ts + "] " + message);
                    }
                }
                catch { }
            }
            catch { }
        }

        private WindowPlacement ResolveTargetPlacement(WorkspaceDefinition workspace, ApplicationDefinition app)
        {
            if (app?.Position == null || app.Position.IsEmpty)
            {
                return default;
            }

            var basePlacement = new WindowPlacement(
                app.Position.X,
                app.Position.Y,
                app.Position.Width,
                app.Position.Height);

            if (_displayManager == null || workspace?.Monitors == null || workspace.Monitors.Count == 0)
            {
                return basePlacement;
            }

            var sourceMonitor = workspace.Monitors.FirstOrDefault(m => m?.Number == app.MonitorIndex)
                ?? workspace.Monitors.FirstOrDefault();
            if (sourceMonitor == null)
            {
                return basePlacement;
            }

            var currentMonitors = _displayManager.GetSnapshot();
            if (currentMonitors.Count == 0)
            {
                return basePlacement;
            }

            var targetMonitor = FindTargetMonitor(currentMonitors, sourceMonitor);
            if (targetMonitor == null)
            {
                return basePlacement;
            }

            var srcRect = GetSourceMonitorRect(sourceMonitor);
            if (srcRect == null || srcRect.Width <= 0 || srcRect.Height <= 0)
            {
                return basePlacement;
            }

            var dstRect = !targetMonitor.DpiAwareRect.IsEmpty
                ? targetMonitor.DpiAwareRect
                : targetMonitor.DpiUnawareRect;
            if (dstRect.Width <= 0 || dstRect.Height <= 0)
            {
                return basePlacement;
            }

            var scaleX = (double)dstRect.Width / srcRect.Width;
            var scaleY = (double)dstRect.Height / srcRect.Height;

            var relX = basePlacement.X - srcRect.Left;
            var relY = basePlacement.Y - srcRect.Top;

            var newX = dstRect.Left + (int)Math.Round(relX * scaleX);
            var newY = dstRect.Top + (int)Math.Round(relY * scaleY);
            var newW = (int)Math.Round(basePlacement.Width * scaleX);
            var newH = (int)Math.Round(basePlacement.Height * scaleY);

            return new WindowPlacement(newX, newY, newW, newH);
        }

        private static DisplayMonitor FindTargetMonitor(
            IReadOnlyList<DisplayMonitor> monitors,
            MonitorDefinition source)
        {
            if (monitors == null || monitors.Count == 0 || source == null)
            {
                return null;
            }

            var match = !string.IsNullOrWhiteSpace(source.Id)
                ? monitors.FirstOrDefault(m => string.Equals(m.Id, source.Id, StringComparison.OrdinalIgnoreCase))
                : null;

            if (match == null && !string.IsNullOrWhiteSpace(source.InstanceId))
            {
                match = monitors.FirstOrDefault(m => string.Equals(m.InstanceId, source.InstanceId, StringComparison.OrdinalIgnoreCase));
            }

            if (match == null)
            {
                match = monitors.FirstOrDefault(m => m.Index == source.Number);
            }

            return match ?? monitors[0];
        }

        private static MonitorDefinition.MonitorRect GetSourceMonitorRect(MonitorDefinition monitor)
        {
            if (monitor == null)
            {
                return null;
            }

            if (monitor.DpiAwareRect != null
                && monitor.DpiAwareRect.Width > 0
                && monitor.DpiAwareRect.Height > 0)
            {
                return monitor.DpiAwareRect;
            }

            if (monitor.DpiUnawareRect != null
                && monitor.DpiUnawareRect.Width > 0
                && monitor.DpiUnawareRect.Height > 0)
            {
                return monitor.DpiUnawareRect;
            }

            return null;
        }

        private async Task TryUpdateLastLaunchedTimeAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken)
        {
            if (workspace == null || _definitionStore == null)
            {
                return;
            }

            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _definitionStore.UpdateLastLaunchedTimeAsync(
                    workspace.Id,
                    timestamp,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static bool IsWindowOnCurrentDesktop(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            if (NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(handle, out var isOnCurrentDesktop))
            {
                return isOnCurrentDesktop;
            }

            return false;
        }

        private bool EnsureWindowOnCurrentDesktop(
            IntPtr handle,
            string appLabel,
            string stage)
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var querySucceeded = NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(handle, out var isOnCurrentDesktop);
            if (querySucceeded && isOnCurrentDesktop)
            {
                return true;
            }

            if (!querySucceeded)
            {
                LogPerf($"WorkspaceRuntime: [{appLabel}] {stage} - desktop query unavailable for handle={handle}; treating as off-desktop");
            }

            LogPerf($"WorkspaceRuntime: [{appLabel}] {stage} - window handle={handle} is not on current virtual desktop");
            return false;
        }

        private readonly record struct FocusCandidate(string Role, IntPtr Handle, string AppLabel);

        private async Task<(bool Success, string Role, IntPtr Handle, string Error)> ApplyFocusPolicyAsync(
            WorkspaceDefinition workspace,
            IReadOnlyList<EnsureAppResult> successfulApps,
            CancellationToken cancellationToken)
        {
            if (successfulApps == null || successfulApps.Count == 0)
            {
                return (false, string.Empty, IntPtr.Zero, "no assigned windows");
            }

            var normalizedPriority = BuildFocusPriority(workspace, successfulApps);
            var candidates = new List<FocusCandidate>();
            var seenHandles = new HashSet<IntPtr>();

            for (var i = 0; i < normalizedPriority.Count; i++)
            {
                var role = normalizedPriority[i];
                var candidate = FindFocusCandidateForRole(successfulApps, role, includeMinimized: false);
                if (!candidate.HasValue || candidate.Value.Handle == IntPtr.Zero)
                {
                    candidate = FindFocusCandidateForRole(successfulApps, role, includeMinimized: true);
                }

                if (!candidate.HasValue || candidate.Value.Handle == IntPtr.Zero || !seenHandles.Add(candidate.Value.Handle))
                {
                    continue;
                }

                candidates.Add(new FocusCandidate(
                    role,
                    candidate.Value.Handle,
                    DescribeApp(candidate.Value.App)));
            }

            var fallback = FindFallbackFocusCandidate(successfulApps, includeMinimized: false, seenHandles);
            if (!fallback.HasValue || fallback.Value.Handle == IntPtr.Zero)
            {
                fallback = FindFallbackFocusCandidate(successfulApps, includeMinimized: true, seenHandles);
            }

            if (fallback.HasValue && fallback.Value.Handle != IntPtr.Zero && seenHandles.Add(fallback.Value.Handle))
            {
                candidates.Add(new FocusCandidate(
                    string.IsNullOrWhiteSpace(fallback.Value.App?.Role) ? "fallback" : fallback.Value.App.Role,
                    fallback.Value.Handle,
                    DescribeApp(fallback.Value.App)));
            }

            if (candidates.Count == 0)
            {
                return (false, string.Empty, IntPtr.Zero, "no live focusable windows");
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                for (var attempt = 1; attempt <= FocusActivationAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (NativeWindowHelper.TryActivateWindow(candidate.Handle))
                    {
                        LogPerf(
                            $"WorkspaceRuntime: Focus - activated role={candidate.Role}, handle={candidate.Handle}, attempts={attempt}, app={candidate.AppLabel}");
                        return (true, candidate.Role, candidate.Handle, string.Empty);
                    }

                    if (attempt < FocusActivationAttempts)
                    {
                        await Task.Delay(FocusActivationRetryInterval, cancellationToken).ConfigureAwait(false);
                    }
                }

                LogPerf(
                    $"WorkspaceRuntime: Focus - activation failed for role={candidate.Role}, handle={candidate.Handle}, app={candidate.AppLabel}");
            }

            return (false, string.Empty, IntPtr.Zero, "activation failed for all candidates");
        }

        private static IReadOnlyList<string> BuildFocusPriority(
            WorkspaceDefinition workspace,
            IReadOnlyList<EnsureAppResult> successfulApps)
        {
            var focus = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (workspace?.FocusPriority != null)
            {
                foreach (var role in workspace.FocusPriority)
                {
                    var normalized = NormalizeRole(role);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    focus.Add(normalized);
                }
            }

            if (focus.Count > 0)
            {
                return focus;
            }

            if (successfulApps != null)
            {
                for (var i = 0; i < successfulApps.Count; i++)
                {
                    var role = NormalizeRole(successfulApps[i].App?.Role);
                    if (string.IsNullOrWhiteSpace(role) || !seen.Add(role))
                    {
                        continue;
                    }

                    focus.Add(role);
                }
            }

            return focus;
        }

        private static EnsureAppResult? FindFocusCandidateForRole(
            IReadOnlyList<EnsureAppResult> successfulApps,
            string role,
            bool includeMinimized)
        {
            if (string.IsNullOrWhiteSpace(role) || successfulApps == null)
            {
                return null;
            }

            for (var i = 0; i < successfulApps.Count; i++)
            {
                var current = successfulApps[i];
                if (!string.Equals(
                    NormalizeRole(current.App?.Role),
                    NormalizeRole(role),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsFocusableCandidate(current, includeMinimized))
                {
                    return current;
                }
            }

            return null;
        }

        private static EnsureAppResult? FindFallbackFocusCandidate(
            IReadOnlyList<EnsureAppResult> successfulApps,
            bool includeMinimized,
            ISet<IntPtr> excludedHandles)
        {
            if (successfulApps == null)
            {
                return null;
            }

            for (var i = 0; i < successfulApps.Count; i++)
            {
                var current = successfulApps[i];
                if (excludedHandles != null && excludedHandles.Contains(current.Handle))
                {
                    continue;
                }

                if (IsFocusableCandidate(current, includeMinimized))
                {
                    return current;
                }
            }

            return null;
        }

        private static bool IsFocusableCandidate(EnsureAppResult result, bool includeMinimized)
        {
            if (!result.Success || result.Handle == IntPtr.Zero)
            {
                return false;
            }

            if (!NativeWindowHelper.IsWindowHandleValid(result.Handle) || NativeWindowHelper.IsWindowCloaked(result.Handle))
            {
                return false;
            }

            if (!includeMinimized && result.App?.Minimized == true)
            {
                return false;
            }

            return IsWindowOnCurrentDesktop(result.Handle);
        }

        private static string NormalizeRole(string role)
        {
            return string.IsNullOrWhiteSpace(role)
                ? string.Empty
                : role.Trim().ToLowerInvariant();
        }

        private static string ToWindowHandleString(IntPtr handle)
        {
            return handle == IntPtr.Zero
                ? string.Empty
                : $"0x{unchecked((ulong)handle.ToInt64()):X}";
        }

        private static void LogSwitchSummary(WorkspaceSwitchDiagnostics result)
        {
            if (result == null)
            {
                return;
            }

            var errors = result.Errors != null && result.Errors.Count > 0
                ? string.Join(" | ", result.Errors)
                : "none";
            var stageDurations = result.StageDurationsMs != null && result.StageDurationsMs.Count > 0
                ? string.Join(", ", result.StageDurationsMs.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}:{kvp.Value}ms"))
                : "none";

            LogPerf(
                "WorkspaceRuntime: Switch summary " +
                $"ok={result.Ok}, workspaceId={result.WorkspaceId}, claimed={result.ClaimedCount}, launched={result.LaunchedCount}, arranged={result.ArrangedCount}, minimized={result.MinimizedCount}, " +
                $"focusedRole={result.FocusedRole}, focusedHwnd={result.FocusedHwnd}, durationMs={result.DurationMs}, stageDurations={stageDurations}, errors={errors}");
        }
    }
}
