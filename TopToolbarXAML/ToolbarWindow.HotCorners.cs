// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
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

        private CapturedBitmap _pendingCapture;
        private RectInt32 _pendingMonitorPx;
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
                _hotCornerService.HoverChanged += state => _cornerOverlay?.Update(state);
                _hotCornerService.ActionTriggered += OnHotCornerActionTriggered;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: initialization failed - {ex.Message}");
            }
        }

        private void SyncCornerOverlayTheme()
        {
            _cornerOverlay?.ApplyTheme(RootGrid?.Resources);
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
                await RunOnUiThreadAsync(() => _hotCornerService.ApplyConfig(hotCorners)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: applying config failed - {ex.Message}");
            }
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
                            _pendingCapture = capture;
                            _pendingMonitorPx = new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                            _pendingScale = context.Scale > 0 ? context.Scale : 1.0;
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
            await EnqueueAsync(async () =>
            {
                try
                {
                    ShowToolbar();
                    await RefreshWorkspaceGroupAsync().ConfigureAwait(true);

                    RectInt32 target = default;
                    var found = false;
                    for (var attempt = 0; attempt < 16 && !found; attempt++)
                    {
                        await DelayOnDispatcherAsync(40).ConfigureAwait(true);
                        found = TryGetWorkspaceButtonScreenRect(workspaceId, out target);
                    }

                    if (_hasPendingCapture && found && _photoFlight != null)
                    {
                        var capture = _pendingCapture;
                        var monitor = _pendingMonitorPx;
                        var scale = _pendingScale;
                        _hasPendingCapture = false;
                        _pendingCapture = default;

                        await _photoFlight.PlayAsync(capture, monitor, target, scale).ConfigureAwait(true);
                    }
                    else
                    {
                        _hasPendingCapture = false;
                        _pendingCapture = default;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"HotCorners: flight animation failed - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    _hasPendingCapture = false;
                    _pendingCapture = default;
                }
            }).ConfigureAwait(false);
        }

        private bool TryGetWorkspaceButtonScreenRect(string workspaceId, out RectInt32 rect)
        {
            rect = default;
            if (string.IsNullOrWhiteSpace(workspaceId) || ToolbarContainer == null || ToolbarContainer.XamlRoot?.Content is not UIElement contentRoot)
            {
                return false;
            }

            var button = FindWorkspaceButtonElement(ToolbarContainer, workspaceId);
            if (button == null || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                var transform = button.TransformToVisual(contentRoot);
                var topLeft = transform.TransformPoint(new Point(0, 0));
                var scale = ToolbarContainer.XamlRoot?.RasterizationScale ?? 1.0;
                if (scale <= 0)
                {
                    scale = 1.0;
                }

                var pos = AppWindow.Position;
                var x = (int)Math.Round(pos.X + (topLeft.X * scale));
                var y = (int)Math.Round(pos.Y + (topLeft.Y * scale));
                var w = (int)Math.Round(button.ActualWidth * scale);
                var h = (int)Math.Round(button.ActualHeight * scale);
                if (w <= 0 || h <= 0)
                {
                    return false;
                }

                rect = new RectInt32(x, y, w, h);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCorners: button rect failed - {ex.Message}");
                return false;
            }
        }

        private static FrameworkElement FindWorkspaceButtonElement(DependencyObject node, string workspaceId)
        {
            if (node == null)
            {
                return null;
            }

            if (node is Button button &&
                button.Tag is ToolbarButtonItem item &&
                TryGetRuntimeWorkspaceId(item.Button, out var id) &&
                string.Equals(id, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                return button;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var match = FindWorkspaceButtonElement(child, workspaceId);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
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

        private Task DelayOnDispatcherAsync(int milliseconds)
        {
            var tcs = new TaskCompletionSource();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.IsRepeating = false;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                tcs.TrySetResult();
            };
            timer.Start();
            return tcs.Task;
        }

        private void DisposeHotCorners()
        {
            try
            {
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
