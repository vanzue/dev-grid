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
