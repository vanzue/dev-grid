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
        private static readonly TimeSpan MinimizeExtraneousBudget = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan LaunchWatchdogBaseTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan LaunchWatchdogPerAppTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan LaunchWatchdogMaxTimeout = TimeSpan.FromMinutes(3);
        private static readonly SemaphoreSlim SwitchGate = new(1, 1);
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
            CancellationToken cancellationToken,
            IProgress<string> progress = null,
            bool allowLaunchMissingWindows = true
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

            var swResolve = Stopwatch.StartNew();
            var workspace = await _definitionStore
                .LoadByIdAsync(normalizedWorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            swResolve.Stop();
            LogPerf(
                $"WorkspaceRuntime: Loaded workspace '{normalizedWorkspaceId}' in {swResolve.ElapsedMilliseconds} ms"
            );
            return await LaunchWorkspaceResolvedAsync(
                workspace,
                normalizedWorkspaceId,
                swResolve.ElapsedMilliseconds,
                recordResolveStage: true,
                updateLastLaunchedTime: true,
                cancellationToken,
                progress,
                allowLaunchMissingWindows).ConfigureAwait(false);
        }

        public Task<WorkspaceSwitchDiagnostics> LaunchWorkspaceAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken,
            IProgress<string> progress = null,
            bool allowLaunchMissingWindows = true)
        {
            ArgumentNullException.ThrowIfNull(workspace);

            if (string.IsNullOrWhiteSpace(workspace.Id))
            {
                workspace.Id = Guid.NewGuid().ToString("N");
            }

            var normalizedWorkspaceId = workspace.Id.Trim();
            return LaunchWorkspaceResolvedAsync(
                workspace,
                normalizedWorkspaceId,
                resolveDurationMs: 0,
                recordResolveStage: false,
                updateLastLaunchedTime: false,
                cancellationToken,
                progress,
                allowLaunchMissingWindows);
        }

        private async Task<WorkspaceSwitchDiagnostics> LaunchWorkspaceResolvedAsync(
            WorkspaceDefinition workspace,
            string normalizedWorkspaceId,
            long resolveDurationMs,
            bool recordResolveStage,
            bool updateLastLaunchedTime,
            CancellationToken cancellationToken,
            IProgress<string> progress,
            bool allowLaunchMissingWindows)
        {
            var queueStopwatch = Stopwatch.StartNew();
            await SwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            queueStopwatch.Stop();
            if (queueStopwatch.ElapsedMilliseconds > 0)
            {
                LogPerf(
                    $"WorkspaceRuntime: switch gate waited {queueStopwatch.ElapsedMilliseconds} ms for workspace '{normalizedWorkspaceId}'.");
            }

            try
            {
                var result = new WorkspaceSwitchDiagnostics
                {
                    WorkspaceId = normalizedWorkspaceId,
                };
                if (recordResolveStage)
                {
                    result.StageDurationsMs["resolve"] = resolveDurationMs;
                }

                var swTotal = Stopwatch.StartNew();
                void ReportProgress(string message)
                {
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    try
                    {
                        progress?.Report(message);
                    }
                    catch
                    {
                        // Progress reporting must never block workspace launch.
                    }
                }

                WorkspaceSwitchDiagnostics FinalizeResult(bool ok)
                {
                    swTotal.Stop();
                    result.Ok = ok;
                    result.DurationMs = swTotal.ElapsedMilliseconds;
                    LogSwitchSummary(result);
                    return result;
                }

                if (workspace == null)
                {
                    result.Errors.Add("not-found: workspace not found.");
                    AppLogger.LogWarning($"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' not found.");
                    return FinalizeResult(false);
                }

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
                ReportProgress($"Launching window set ({appCount} app(s))...");

                if (appCount == 0)
                {
                    result.Errors.Add("not-found: workspace has no applications.");
                    AppLogger.LogWarning($"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' has no applications to launch.");
                    return FinalizeResult(false);
                }

                var watchdogTimeout = ResolveLaunchWatchdogTimeout(appCount);
                using var launchCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                launchCancellationSource.CancelAfter(watchdogTimeout);
                var launchCancellationToken = launchCancellationSource.Token;

                LogPerf(
                    $"WorkspaceRuntime: Launch watchdog configured to {watchdogTimeout.TotalSeconds:0}s for '{normalizedWorkspaceId}'");

                try
                {
                var preMinimizedCount = 0;
                var shouldPreMinimize = allowLaunchMissingWindows
                    && workspace.MoveExistingWindows
                    && apps.Any(app => ShouldForceLaunchNewIfUnbound(workspace, app));
                if (shouldPreMinimize)
                {
                    ReportProgress("Phase 1/4: Minimizing existing windows...");
                    var swPhase0 = Stopwatch.StartNew();
                    LogPerf("WorkspaceRuntime: Phase 0 - Minimize existing windows before launching fresh workspace windows");
                    preMinimizedCount = MinimizeExtraneousWindows(new HashSet<IntPtr>(), null, launchCancellationToken);
                    swPhase0.Stop();
                    result.StageDurationsMs["preMinimize"] = swPhase0.ElapsedMilliseconds;
                    LogPerf($"WorkspaceRuntime: Phase 0 done in {swPhase0.ElapsedMilliseconds} ms - minimized {preMinimizedCount} window(s)");
                }

                // ============================================================
                // Phase 1: Ensure all apps alive (two-pass approach)
                // - Pass 1: Assign existing windows to apps (no launching)
                // - Pass 2: Launch new windows for apps that didn't get one
                // ============================================================
                var swPhase1 = Stopwatch.StartNew();
                LogPerf("WorkspaceRuntime: Phase 1 - Ensure all apps alive");
                ReportProgress("Phase 2/4: Resolving existing windows...");

                var allResults = new List<EnsureAppResult>();
                var appsNeedingLaunch = new List<ApplicationDefinition>();

                if (workspace.MoveExistingWindows)
                {
                    // Pass 1: Bind deterministic windows from registry only (no fuzzy claiming).
                    LogPerf("WorkspaceRuntime: Phase 1 Pass 1 - Bind registered workspace windows");
                    var assignTasks = apps
                        .Select(app => TryAssignExistingWindowWithTimeoutAsync(
                            app,
                            ShouldForceLaunchNewIfUnbound(workspace, app),
                            launchCancellationToken))
                        .ToList();

                    var assignResults = await Task.WhenAll(assignTasks).ConfigureAwait(false);

                    var skippedMissingLaunches = 0;
                    for (int i = 0; i < apps.Count; i++)
                    {
                        var assignResult = assignResults[i];
                        if (assignResult.Success)
                        {
                            allResults.Add(assignResult);
                        }
                        else
                        {
                            if (allowLaunchMissingWindows)
                            {
                                appsNeedingLaunch.Add(apps[i]);
                            }
                            else
                            {
                                skippedMissingLaunches++;
                                LogPerf($"WorkspaceRuntime: [{DescribeApp(apps[i])}] switch-only mode: no bound window found; launch skipped.");
                            }
                        }
                    }

                    LogPerf($"WorkspaceRuntime: Phase 1 Pass 1 done - {allResults.Count} apps bound from registry, {appsNeedingLaunch.Count} need launch");
                    if (!allowLaunchMissingWindows && skippedMissingLaunches > 0)
                    {
                        LogPerf($"WorkspaceRuntime: Phase 1 Pass 1 switch-only mode skipped launch for {skippedMissingLaunches} missing app(s).");
                    }
                }
                else
                {
                    appsNeedingLaunch.AddRange(apps);
                }

                // Pass 2: Launch new windows for remaining apps (sequential to avoid race)
                if (appsNeedingLaunch.Count > 0)
                {
                    LogPerf($"WorkspaceRuntime: Phase 1 Pass 2 - Launch {appsNeedingLaunch.Count} new windows");
                    var launchIndex = 0;
                    foreach (var app in appsNeedingLaunch)
                    {
                        launchIndex++;
                        var appName = string.IsNullOrWhiteSpace(app?.Role)
                            ? (string.IsNullOrWhiteSpace(app?.Name) ? "app" : app.Name.Trim())
                            : app.Role.Trim();
                        ReportProgress($"Phase 3/4: Starting {appName} ({launchIndex}/{appsNeedingLaunch.Count})...");
                        launchCancellationToken.ThrowIfCancellationRequested();
                        var launchResult = await LaunchNewWindowAsync(
                            app,
                            normalizedWorkspaceId,
                            ShouldForceLaunchNewIfUnbound(workspace, app),
                            launchCancellationToken).ConfigureAwait(false);
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
                LogPerf("WorkspaceRuntime: Phase 2 - Resize all windows (parallel)");
                ReportProgress("Phase 4/4: Arranging windows...");

                var workspaceWindowMinimizeStates = successfulApps
                    .Where(r => r.Handle != IntPtr.Zero)
                    .GroupBy(r => r.Handle)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Any(item => item.App?.Minimized == true));

                var resizeTasks = successfulApps
                    .Select(r =>
                        ResizeWindowWithTimeoutAsync(
                            r.Handle,
                            r.App,
                            ResolveTargetPlacement(workspace, r.App),
                            r.LaunchedNew,
                            workspaceWindowMinimizeStates,
                            launchCancellationToken))
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
                    if (shouldPreMinimize)
                    {
                        result.MinimizedCount = preMinimizedCount;
                        LogPerf("WorkspaceRuntime: Phase 3 - skipped post-launch minimize because pre-minimize already ran for fresh-launch workspace.");
                    }
                    else
                    {
                        var workspaceHandles = new HashSet<IntPtr>(successfulApps.Select(r => r.Handle));
                        var workspaceProcessIds = GetWorkspaceProcessIds(successfulApps);
                        result.MinimizedCount = MinimizeExtraneousWindows(workspaceHandles, workspaceProcessIds, launchCancellationToken);
                        LogPerf($"WorkspaceRuntime: Phase 3 - MinimizeExtraneousWindows minimized {result.MinimizedCount} window(s)");
                    }
                }
                swPhase3.Stop();
                result.StageDurationsMs["minimize"] = swPhase3.ElapsedMilliseconds;

                // ============================================================
                // Phase 4: Focus target window using role priority with fallback.
                // ============================================================
                var swPhase4 = Stopwatch.StartNew();
                if (successfulApps.Count > 0)
                {
                    ReportProgress("Finalizing focus...");
                    var focus = await ApplyFocusPolicyAsync(workspace, successfulApps, launchCancellationToken).ConfigureAwait(false);
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

                    if (updateLastLaunchedTime)
                    {
                        await TryUpdateLastLaunchedTimeAsync(workspace, cancellationToken).ConfigureAwait(false);
                    }

                    return FinalizeResult(true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Errors.Add(
                        $"launch-timeout: workspace switch exceeded {watchdogTimeout.TotalSeconds:0}s.");
                    AppLogger.LogWarning(
                        $"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' launch timed out after {watchdogTimeout.TotalSeconds:0}s.");
                    return FinalizeResult(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"launch-failed: {ex.Message}");
                    AppLogger.LogError(
                        $"WorkspaceRuntime: workspace '{normalizedWorkspaceId}' launch failed with exception.",
                        ex);
                    return FinalizeResult(false);
                }
            }
            finally
            {
                SwitchGate.Release();
            }
        }

        private static bool ShouldForceLaunchNewIfUnbound(WorkspaceDefinition workspace, ApplicationDefinition app)
        {
            if (app?.LaunchNewIfUnbound == true)
            {
                return true;
            }

            // Backward compatibility: template-generated workspaces created before
            // launch-new-if-unbound existed should still launch dedicated instances.
            return !string.IsNullOrWhiteSpace(workspace?.TemplateName);
        }

        private static TimeSpan ResolveLaunchWatchdogTimeout(int appCount)
        {
            var safeAppCount = Math.Max(0, appCount);
            var computed = LaunchWatchdogBaseTimeout
                + TimeSpan.FromTicks(LaunchWatchdogPerAppTimeout.Ticks * safeAppCount);
            return computed <= LaunchWatchdogMaxTimeout
                ? computed
                : LaunchWatchdogMaxTimeout;
        }

    }
}
