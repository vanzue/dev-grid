// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services.Display;
using TopToolbar.Services.HotCorners;
using TopToolbar.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private DisplayManager _hotCornerDisplayManager;
        private HotCornerService _hotCornerService;
        private CornerOverlayWindow _cornerOverlay;
        private HotCornerActionRouter _hotCornerRouter;
        private PhotoFlightWindow _photoFlight;
        private readonly List<CornerHintOverlay> _cornerHintOverlays = new();
        private string _cornerHintsSignature = string.Empty;
        private HotCorner? _suppressedHintCorner;
        private DisplayRect _suppressedHintBounds;

        private CapturedBitmap _pendingCapture;
        private RectInt32 _pendingMonitorPx;
        private RectInt32 _pendingCornerTargetPx;
        private double _pendingScale = 1.0;
        private bool _hasPendingCapture;

        private void InitializeHotCorners()
        {
            try
            {
                _hotCornerDisplayManager = new DisplayManager();
                _cornerOverlay = new CornerOverlayWindow();
                _photoFlight = new PhotoFlightWindow();
                _hotCornerRouter = new HotCornerActionRouter(_notificationService);
                _hotCornerRouter.SnapshotCompleted += OnHotCornerSnapshotCompletedAsync;

                _hotCornerService = new HotCornerService(DispatcherQueue, _hotCornerDisplayManager);
                _hotCornerDisplayManager.MonitorsChanged += OnHotCornerMonitorsChanged;
                _hotCornerService.HoverChanged += OnHotCornerHoverChanged;
                _hotCornerService.ActionTriggered += OnHotCornerActionTriggered;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: initialization failed - {ex.Message}");
            }
        }

        private void OnHotCornerMonitorsChanged(object sender, EventArgs e)
        {
            _ = ApplyHotCornersConfigAsync();
        }

        private void SyncCornerOverlayTheme()
        {
            _cornerOverlay?.ApplyTheme(RootGrid?.Resources);
            foreach (var hint in _cornerHintOverlays)
            {
                hint.Window.ApplyTheme(RootGrid?.Resources);
            }
        }

        private async Task ApplyHotCornersConfigAsync()
        {
            if (_hotCornerService == null)
            {
                return;
            }

            try
            {
                var config = await _configService.LoadAsync().ConfigureAwait(false);
                var hotCorners = config?.HotCorners;
                await RunOnUiThreadAsync(() =>
                {
                    _hotCornerService.ApplyConfig(hotCorners);
                    UpdateCornerHints(hotCorners);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: applying config failed - {ex.Message}");
            }
        }

        private void OnHotCornerHoverChanged(HotCornerHoverState state)
        {
            if (state.Active)
            {
                if (TryAnimateMatchingCornerHint(state))
                {
                    _cornerOverlay?.Hide();
                    return;
                }
            }
            else
            {
                if (TryRestoreAnimatedCornerHint())
                {
                    _cornerOverlay?.Hide();
                    return;
                }
            }

            _cornerOverlay?.Update(state);

            if (state.Active)
            {
                SuppressCornerHint(state.Corner, state.MonitorBounds);
            }
            else
            {
                RestoreSuppressedCornerHint();
            }
        }

        private bool TryAnimateMatchingCornerHint(HotCornerHoverState state)
        {
            foreach (var hint in _cornerHintOverlays)
            {
                if (hint.Corner != state.Corner || !hint.Bounds.Equals(state.MonitorBounds))
                {
                    continue;
                }

                _suppressedHintCorner = state.Corner;
                _suppressedHintBounds = state.MonitorBounds;
                hint.Window.Update(state);
                hint.IsSuppressed = false;
                return true;
            }

            return false;
        }

        private bool TryRestoreAnimatedCornerHint()
        {
            if (_suppressedHintCorner == null)
            {
                return false;
            }

            foreach (var hint in _cornerHintOverlays)
            {
                if (hint.Corner != _suppressedHintCorner.Value || !hint.Bounds.Equals(_suppressedHintBounds))
                {
                    continue;
                }

                hint.Window.ShowHint(hint.Corner, hint.Bounds, hint.Scale, hint.Label);
                hint.IsSuppressed = false;
                _suppressedHintCorner = null;
                _suppressedHintBounds = default;
                return true;
            }

            _suppressedHintCorner = null;
            _suppressedHintBounds = default;
            return false;
        }

        private void OnHotCornerActionTriggered(HotCornerActionContext context)
        {
            // Runs on the UI dispatcher (poll timer). Capture the screen now, before the snapshot runs,
            // so the "photo" reflects exactly what the user saw at the moment of triggering.
            try
            {
                if (string.Equals(context.ActionId, HotCornerActions.Snapshot, StringComparison.OrdinalIgnoreCase))
                {
                    var bounds = context.MonitorBounds;
                    if (!bounds.IsEmpty)
                    {
                        var capture = ScreenCaptureService.Capture(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                        if (capture.IsValid)
                        {
                            var monitorPx = new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                            _pendingCapture = capture;
                            _pendingMonitorPx = monitorPx;
                            _pendingScale = context.Scale > 0 ? context.Scale : 1.0;
                            _pendingCornerTargetPx = BuildCornerTargetRect(context.Corner, monitorPx);
                            _hasPendingCapture = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: capture failed - {ex.Message}");
            }

            _ = _hotCornerRouter.ExecuteAsync(context.ActionId);
        }

        private async Task OnHotCornerSnapshotCompletedAsync(string workspaceId, string workspaceName)
        {
            var hasCapture = _hasPendingCapture;
            var capture = _pendingCapture;
            var monitor = _pendingMonitorPx;
            var scale = _pendingScale;
            var target = _pendingCornerTargetPx;

            _hasPendingCapture = false;
            _pendingCapture = default;

            // Keep the workspace list current, but the corner flight does not depend on it.
            _ = RefreshWorkspaceGroupAsync();

            if (!hasCapture || _photoFlight == null)
            {
                return;
            }

            await EnqueueAsync(async () =>
            {
                try
                {
                    await _photoFlight.PlayAsync(capture, monitor, target, scale).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"HotCorners: flight animation failed - {ex.GetType().Name}: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        private Task EnqueueAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await work().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"HotCorners: ui work failed - {ex.Message}");
                }
                finally
                {
                    tcs.TrySetResult();
                }
            }))
            {
                tcs.TrySetResult();
            }

            return tcs.Task;
        }

        private void UpdateCornerHints(HotCornersConfig config)
        {
            if (config?.Enabled != true || config.ShowCornerHints != true || config.Actions == null || _hotCornerDisplayManager == null)
            {
                ClearCornerHints();
                return;
            }

            var monitors = _hotCornerDisplayManager.GetSnapshot();
            if (monitors.Count == 0)
            {
                AppLogger.LogInfo("HotCornersDisplaySnapshot: no monitors found while updating corner hints.");
                ClearCornerHints();
                return;
            }

            AppLogger.LogInfo($"HotCornersDisplaySnapshot: monitorCount={monitors.Count}, hintsEnabled={config.ShowCornerHints}, enabled={config.Enabled}.");
            LogHotCornersDisplayAreaSnapshot();

            var descriptors = new List<CornerHintDescriptor>();
            foreach (var monitor in monitors)
            {
                var originalBounds = monitor.Bounds;
                if (originalBounds.IsEmpty)
                {
                    AppLogger.LogInfo(
                        $"HotCornersDisplaySnapshot: monitor[{monitor.Index}] id='{monitor.Id}', instance='{monitor.InstanceId}' skipped empty dpiAware=({monitor.DpiAwareRect.Left},{monitor.DpiAwareRect.Top},{monitor.DpiAwareRect.Width},{monitor.DpiAwareRect.Height}).");
                    continue;
                }

                var bounds = ResolveHotCornerHintBounds(monitor, out var scale, out var boundsSource);
                AppLogger.LogInfo(
                    $"HotCornersDisplaySnapshot: monitor[{monitor.Index}] id='{monitor.Id}', instance='{monitor.InstanceId}', dpi={monitor.Dpi}, chosenSource={boundsSource}, chosenScale={scale:F3}, chosenBounds=({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}) rb=({bounds.Right},{bounds.Bottom}), dpiAware=({monitor.DpiAwareRect.Left},{monitor.DpiAwareRect.Top},{monitor.DpiAwareRect.Width},{monitor.DpiAwareRect.Height}) rb=({monitor.DpiAwareRect.Right},{monitor.DpiAwareRect.Bottom}), dpiUnaware=({monitor.DpiUnawareRect.Left},{monitor.DpiUnawareRect.Top},{monitor.DpiUnawareRect.Width},{monitor.DpiUnawareRect.Height}), workAware=({monitor.DpiAwareWorkRect.Left},{monitor.DpiAwareWorkRect.Top},{monitor.DpiAwareWorkRect.Width},{monitor.DpiAwareWorkRect.Height}), workUnaware=({monitor.DpiUnawareWorkRect.Left},{monitor.DpiUnawareWorkRect.Top},{monitor.DpiUnawareWorkRect.Width},{monitor.DpiUnawareWorkRect.Height}).");

                foreach (var corner in Enum.GetValues<HotCorner>())
                {
                    if (!TryGetCornerAction(config, corner, out var actionId))
                    {
                        AppLogger.LogInfo(
                            $"HotCornersDisplaySnapshot: monitor[{monitor.Index}] corner={corner} skipped action=none.");
                        continue;
                    }

                    var label = GetHotCornerActionLabel(actionId);
                    descriptors.Add(new CornerHintDescriptor(corner, bounds, scale, label));
                    try
                    {
                        AppLogger.LogInfo(
                            $"HotCornersDisplaySnapshot: monitor[{monitor.Index}] corner={corner} action='{actionId}' showing hint.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"HotCorners: hint overlay failed - {ex.Message}");
                    }
                }

            }

            var signature = BuildCornerHintsSignature(descriptors);
            if (string.Equals(signature, _cornerHintsSignature, StringComparison.Ordinal))
            {
                RefreshCornerHintsInPlace(descriptors);
                return;
            }

            ClearCornerHints();
            _cornerHintsSignature = signature;

            foreach (var descriptor in descriptors)
            {
                try
                {
                    var hint = new CornerOverlayWindow();
                    hint.ApplyTheme(RootGrid?.Resources);
                    hint.ShowHint(descriptor.Corner, descriptor.Bounds, descriptor.Scale, descriptor.Label);
                    _cornerHintOverlays.Add(new CornerHintOverlay(
                        descriptor.Corner,
                        descriptor.Bounds,
                        descriptor.Scale,
                        descriptor.Label,
                        hint));
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"HotCorners: hint overlay failed - {ex.Message}");
                }
            }
        }

        private void RefreshCornerHintsInPlace(IReadOnlyList<CornerHintDescriptor> descriptors)
        {
            if (descriptors == null || descriptors.Count != _cornerHintOverlays.Count)
            {
                return;
            }

            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                var hint = _cornerHintOverlays[i];

                hint.Corner = descriptor.Corner;
                hint.Bounds = descriptor.Bounds;
                hint.Scale = descriptor.Scale;
                hint.Label = descriptor.Label;

                if (!hint.IsSuppressed)
                {
                    hint.Window.ShowHint(descriptor.Corner, descriptor.Bounds, descriptor.Scale, descriptor.Label);
                }
            }
        }

        private void SuppressCornerHint(HotCorner corner, DisplayRect bounds)
        {
            if (_suppressedHintCorner == corner && _suppressedHintBounds.Equals(bounds))
            {
                return;
            }

            RestoreSuppressedCornerHint();
            _suppressedHintCorner = corner;
            _suppressedHintBounds = bounds;

            foreach (var hint in _cornerHintOverlays)
            {
                if (hint.Corner == corner && hint.Bounds.Equals(bounds))
                {
                    hint.Window.Hide();
                    hint.IsSuppressed = true;
                }
            }
        }

        private void RestoreSuppressedCornerHint()
        {
            if (_suppressedHintCorner == null)
            {
                return;
            }

            foreach (var hint in _cornerHintOverlays)
            {
                if (hint.IsSuppressed)
                {
                    hint.Window.ShowHint(hint.Corner, hint.Bounds, hint.Scale, hint.Label);
                    hint.IsSuppressed = false;
                }
            }

            _suppressedHintCorner = null;
            _suppressedHintBounds = default;
        }

        private static DisplayRect ResolveHotCornerHintBounds(DisplayMonitor monitor, out double scale, out string source)
        {
            var logicalBounds = monitor.Bounds;
            scale = monitor.Dpi > 0 ? monitor.Dpi / 96.0 : 1.0;
            source = "DisplayManager";

            try
            {
                var rect = new RectInt32(
                    logicalBounds.Left,
                    logicalBounds.Top,
                    Math.Max(1, logicalBounds.Width),
                    Math.Max(1, logicalBounds.Height));
                var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
                if (area == null)
                {
                    var probe = new PointInt32(
                        logicalBounds.Left + Math.Max(1, logicalBounds.Width / 2),
                        logicalBounds.Top + Math.Max(1, logicalBounds.Height / 2));
                    area = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.None);
                }

                if (area != null)
                {
                    var outer = area.OuterBounds;
                    if (outer.Width > 0 && outer.Height > 0)
                    {
                        source = "DisplayArea";
                        scale = 1.0;
                        return new DisplayRect(outer.X, outer.Y, outer.Width, outer.Height);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCornersDisplaySnapshot: failed to resolve DisplayArea bounds for monitor[{monitor.Index}] - {ex.Message}");
            }

            return logicalBounds;
        }

        private void LogHotCornersDisplayAreaSnapshot()
        {
            try
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var outer = area.OuterBounds;
                var work = area.WorkArea;
                AppLogger.LogInfo(
                    $"HotCornersDisplayArea: toolbarWindowId={AppWindow.Id.Value}, displayId={area.DisplayId.Value}, outer=({outer.X},{outer.Y},{outer.Width},{outer.Height}) rb=({outer.X + outer.Width},{outer.Y + outer.Height}), work=({work.X},{work.Y},{work.Width},{work.Height}) rb=({work.X + work.Width},{work.Y + work.Height}).");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCornersDisplayArea: failed - {ex.Message}");
            }
        }

        private static bool TryGetCornerAction(HotCornersConfig config, HotCorner corner, out string actionId)
        {
            actionId = null;
            if (!config.Actions.TryGetValue(corner, out var action) ||
                string.IsNullOrWhiteSpace(action) ||
                string.Equals(action, HotCornerActions.None, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            actionId = action;
            return true;
        }

        private static string GetHotCornerActionLabel(string actionId)
        {
            return actionId switch
            {
                HotCornerActions.Snapshot => "Snap workspace",
                HotCornerActions.ShowDesktop => "Show desktop",
                HotCornerActions.TaskView => "Task view",
                HotCornerActions.LockScreen => "Lock screen",
                HotCornerActions.StartScreenSaver => "Screen saver",
                HotCornerActions.TurnOffDisplay => "Display off",
                _ => "Hot corner",
            };
        }

        private void ClearCornerHints()
        {
            _cornerHintsSignature = string.Empty;
            _suppressedHintCorner = null;
            _suppressedHintBounds = default;

            foreach (var hint in _cornerHintOverlays)
            {
                try
                {
                    hint.Window.Dispose();
                }
                catch
                {
                }
            }

            _cornerHintOverlays.Clear();
        }

        private static string BuildCornerHintsSignature(IReadOnlyList<CornerHintDescriptor> descriptors)
        {
            if (descriptors == null || descriptors.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                var bounds = descriptor.Bounds;
                parts.Add(
                    $"{descriptor.Corner}:{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}:{descriptor.Scale:F3}:{descriptor.Label}");
            }

            return string.Join("|", parts);
        }

        private readonly struct CornerHintDescriptor
        {
            public CornerHintDescriptor(HotCorner corner, DisplayRect bounds, double scale, string label)
            {
                Corner = corner;
                Bounds = bounds;
                Scale = scale;
                Label = label;
            }

            public HotCorner Corner { get; }

            public DisplayRect Bounds { get; }

            public double Scale { get; }

            public string Label { get; }
        }

        private sealed class CornerHintOverlay
        {
            public CornerHintOverlay(HotCorner corner, DisplayRect bounds, double scale, string label, CornerOverlayWindow window)
            {
                Corner = corner;
                Bounds = bounds;
                Scale = scale;
                Label = label;
                Window = window;
            }

            public HotCorner Corner { get; set; }

            public DisplayRect Bounds { get; set; }

            public double Scale { get; set; }

            public string Label { get; set; }

            public CornerOverlayWindow Window { get; }

            public bool IsSuppressed { get; set; }
        }

        private void DisposeHotCorners()
        {
            ClearCornerHints();

            try
            {
                if (_hotCornerDisplayManager != null)
                {
                    _hotCornerDisplayManager.MonitorsChanged -= OnHotCornerMonitorsChanged;
                }

                _hotCornerService?.Dispose();
            }
            catch
            {
            }

            try
            {
                _cornerOverlay?.Dispose();
            }
            catch
            {
            }

            try
            {
                _photoFlight?.Dispose();
            }
            catch
            {
            }

            try
            {
                _hotCornerDisplayManager?.Dispose();
            }
            catch
            {
            }
        }
    }
}
