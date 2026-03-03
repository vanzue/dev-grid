// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan WindowArrangeTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan WindowArrangePollInterval = TimeSpan.FromMilliseconds(300);
        private const int WindowArrangeStableChecks = 2;
        private const int WindowArrangeTolerancePixels = 8;
        private static readonly TimeSpan WindowPostSettleTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan WindowPostSettlePollInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan LaunchWindowSettleTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LaunchWindowSettlePollInterval = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan FocusActivationRetryInterval = TimeSpan.FromMilliseconds(150);
        private const int FocusActivationAttempts = 3;

        private readonly WorkspaceDefinitionStore _definitionStore;
        private readonly WindowManager _windowManager;
        private readonly ManagedWindowRegistry _managedWindows;
        private readonly DisplayManager _displayManager;

        public WorkspaceLauncher(
            WorkspaceDefinitionStore definitionStore,
            WindowManager windowManager,
            ManagedWindowRegistry managedWindows,
            DisplayManager displayManager)
        {
            _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _managedWindows = managedWindows ?? throw new ArgumentNullException(nameof(managedWindows));
            _displayManager = displayManager;
        }

        public async Task<WorkspaceSwitchDiagnostics> LaunchWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                throw new ArgumentException(
                    "Workspace ID cannot be null or empty",
                    nameof(workspaceId)
                );
            }

            var normalizedWorkspaceId = workspaceId.Trim();
            var result = new WorkspaceSwitchDiagnostics
            {
                WorkspaceId = normalizedWorkspaceId,
            };

            var swTotal = Stopwatch.StartNew();
            WorkspaceSwitchDiagnostics FinalizeResult(bool ok)
            {
                swTotal.Stop();
                result.Ok = ok;
                result.DurationMs = swTotal.ElapsedMilliseconds;
                LogSwitchSummary(result);
                return result;
            }

            var swResolve = Stopwatch.StartNew();
            var workspace = await _definitionStore
                .LoadByIdAsync(normalizedWorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            swResolve.Stop();
            result.StageDurationsMs["resolve"] = swResolve.ElapsedMilliseconds;
            LogPerf(
                $"WorkspaceRuntime: Loaded workspace '{normalizedWorkspaceId}' in {swResolve.ElapsedMilliseconds} ms"
            );
            if (workspace == null)
            {
                result.Errors.Add("not-found: workspace not found.");
                AppLogger.LogWarning($"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' not found.");
                return FinalizeResult(false);
            }

            {
                var apps = new List<ApplicationDefinition>();
                if (workspace.Applications != null)
                {
                    for (var i = 0; i < workspace.Applications.Count; i++)
                    {
                        var app = workspace.Applications[i];
                        if (app == null)
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(app.Id))
                        {
                            app.Id = Guid.NewGuid().ToString("N");
                        }

                        apps.Add(app);
                    }
                }

                var appCount = apps.Count;
                LogPerf($"WorkspaceRuntime: Starting launch of {appCount} app(s) for '{normalizedWorkspaceId}'");

                if (appCount == 0)
                {
                    result.Errors.Add("not-found: workspace has no applications.");
                    AppLogger.LogWarning($"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' has no applications to launch.");
                    return FinalizeResult(false);
                }

                // ============================================================
                // Phase 1: Ensure all apps alive (two-pass approach)
                // - Pass 1: Assign existing windows to apps (no launching)
                // - Pass 2: Launch new windows for apps that didn't get one
                // ============================================================
                var swPhase1 = Stopwatch.StartNew();
                LogPerf($"WorkspaceRuntime: Phase 1 - Ensure all apps alive");
                
                var allResults = new List<EnsureAppResult>();
                var appsNeedingLaunch = new List<ApplicationDefinition>();
                
                if (workspace.MoveExistingWindows)
                {
                    var snapshot = _windowManager.GetSnapshot();
                    var snapshotIndex = BuildWindowSnapshotIndex(snapshot);

                    // Pass 1: Try to assign existing windows to all apps (parallel, no launching)
                    LogPerf($"WorkspaceRuntime: Phase 1 Pass 1 - Assign existing windows");
                    var assignTasks = apps
                        .Select(app => TryAssignExistingWindowAsync(app, normalizedWorkspaceId, snapshot, snapshotIndex, cancellationToken))
                        .ToList();
                    
                    var assignResults = await Task.WhenAll(assignTasks).ConfigureAwait(false);
                    
                    for (int i = 0; i < apps.Count; i++)
                    {
                        var assignResult = assignResults[i];
                        if (assignResult.Success)
                        {
                            allResults.Add(assignResult);
                        }
                        else
                        {
                            appsNeedingLaunch.Add(apps[i]);
                        }
                    }
                    
                    LogPerf($"WorkspaceRuntime: Phase 1 Pass 1 done - {allResults.Count} apps got existing windows, {appsNeedingLaunch.Count} need launch");
                }
                else
                {
                    appsNeedingLaunch.AddRange(apps);
                }
                
                // Pass 2: Launch new windows for remaining apps (sequential to avoid race)
                if (appsNeedingLaunch.Count > 0)
                {
                    LogPerf($"WorkspaceRuntime: Phase 1 Pass 2 - Launch {appsNeedingLaunch.Count} new windows");
                    foreach (var app in appsNeedingLaunch)
                    {
                        var launchResult = await LaunchNewWindowAsync(app, normalizedWorkspaceId, cancellationToken).ConfigureAwait(false);
                        allResults.Add(launchResult);
                    }
                }
                
                swPhase1.Stop();
                result.StageDurationsMs["claimLaunch"] = swPhase1.ElapsedMilliseconds;
                
                var minimizedMissingCount = allResults.Count(r =>
                    r.Success
                    && r.Handle == IntPtr.Zero
                    && r.App?.Minimized == true);
                var successfulApps = allResults
                    .Where(r => r.Success && r.Handle != IntPtr.Zero)
                    .ToList();
                result.ClaimedCount = successfulApps.Count(r => !r.LaunchedNew);
                result.LaunchedCount = successfulApps.Count(r => r.LaunchedNew);
                LogPerf($"WorkspaceRuntime: Phase 1 done in {swPhase1.ElapsedMilliseconds} ms - {successfulApps.Count}/{appCount} apps ready, minimized-missing={minimizedMissingCount}");

                if (successfulApps.Count == 0 && minimizedMissingCount == 0)
                {
                    result.Errors.Add("launch-timeout: no windows were assigned or launched.");
                    return FinalizeResult(false);
                }

                // ============================================================
                // Phase 2: Resize all windows (parallel)
                // - All windows resize simultaneously
                // - No competition since each window is independent
                // ============================================================
                var swPhase2 = Stopwatch.StartNew();
                LogPerf($"WorkspaceRuntime: Phase 2 - Resize all windows (parallel)");
                
                var resizeTasks = successfulApps
                    .Select(r =>
                        ResizeWindowAsync(
                            r.Handle,
                            r.App,
                            ResolveTargetPlacement(workspace, r.App),
                            r.LaunchedNew,
                            cancellationToken))
                    .ToList();
                
                var arrangedResults = await Task.WhenAll(resizeTasks).ConfigureAwait(false);
                result.ArrangedCount = arrangedResults.Count(arranged => arranged);
                swPhase2.Stop();
                result.StageDurationsMs["arrange"] = swPhase2.ElapsedMilliseconds;
                LogPerf($"WorkspaceRuntime: Phase 2 done in {swPhase2.ElapsedMilliseconds} ms");

                // ============================================================
                // Phase 3: Minimize extraneous windows
                // ============================================================
                var swPhase3 = Stopwatch.StartNew();
                if (workspace.MoveExistingWindows)
                {
                    var workspaceHandles = new HashSet<IntPtr>(successfulApps.Select(r => r.Handle));
                    var workspaceProcessIds = GetWorkspaceProcessIds(successfulApps);
                    result.MinimizedCount = MinimizeExtraneousWindows(workspaceHandles, workspaceProcessIds);
                    LogPerf($"WorkspaceRuntime: Phase 3 - MinimizeExtraneousWindows minimized {result.MinimizedCount} window(s)");
                }
                swPhase3.Stop();
                result.StageDurationsMs["minimize"] = swPhase3.ElapsedMilliseconds;

                // ============================================================
                // Phase 4: Focus target window using role priority with fallback.
                // ============================================================
                var swPhase4 = Stopwatch.StartNew();
                if (successfulApps.Count > 0)
                {
                    var focus = await ApplyFocusPolicyAsync(workspace, successfulApps, cancellationToken).ConfigureAwait(false);
                    swPhase4.Stop();
                    result.StageDurationsMs["focus"] = swPhase4.ElapsedMilliseconds;
                    if (focus.Success)
                    {
                        result.FocusedRole = focus.Role;
                        result.FocusedHwnd = ToWindowHandleString(focus.Handle);
                    }
                    else
                    {
                        result.Errors.Add($"focus-failed: {focus.Error}");
                    }
                }
                else
                {
                    swPhase4.Stop();
                    result.StageDurationsMs["focus"] = swPhase4.ElapsedMilliseconds;
                    LogPerf("WorkspaceRuntime: Phase 4 - skipped focus because all matching apps are minimized/missing.");
                }

                await TryUpdateLastLaunchedTimeAsync(workspace, cancellationToken).ConfigureAwait(false);
                return FinalizeResult(true);
            }
        }

    }
}
