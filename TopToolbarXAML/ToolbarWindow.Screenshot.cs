// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Providers;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private ScreenshotOverlayWindow _screenshotOverlay;

        private static bool IsScreenshotAction(ToolbarButton button)
        {
            var action = button?.Action;
            return action != null &&
                   action.Type == ToolbarActionType.Provider &&
                   string.Equals(action.ProviderId, ScreenshotProvider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(action.ProviderActionId, ScreenshotProvider.CaptureActionId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Freezes the screen of the monitor under the cursor and shows the interactive screenshot
        /// overlay. The toolbar and ring are hidden first so they are not part of the capture.
        /// </summary>
        private async Task LaunchScreenshotCaptureAsync()
        {
            try
            {
                HideToolbar();
                if (_isRadialVisible)
                {
                    HideRadialMenu();
                }

                // Let the compositor remove the toolbar/ring before grabbing pixels.
                await DelayOnUiAsync(80).ConfigureAwait(true);

                GetCursorPos(out var pt);
                if (!TryCaptureMonitorForPoint(pt.X, pt.Y, out var capture, out var monitorPx, out var scale))
                {
                    _notificationService.ShowError("Screenshot failed: could not capture the screen.");
                    return;
                }

                try
                {
                    _screenshotOverlay?.Close();
                }
                catch
                {
                }

                var overlay = new ScreenshotOverlayWindow(capture, monitorPx, scale);
                _screenshotOverlay = overlay;
                overlay.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_screenshotOverlay, overlay))
                    {
                        _screenshotOverlay = null;
                    }
                };
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Screenshot: launch failed.", ex);
                _notificationService.ShowError("Screenshot failed: " + ex.Message);
            }
        }

        // UI-thread-affine delay backed by a DispatcherQueueTimer so the continuation stays on the
        // UI thread (avoids the thread-pool resume pitfall of Task.Delay inside dispatcher callbacks).
        private Task DelayOnUiAsync(int milliseconds)
        {
            var tcs = new TaskCompletionSource();
            var dispatcher = DispatcherQueue;
            if (dispatcher == null)
            {
                tcs.TrySetResult();
                return tcs.Task;
            }

            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                s.Stop();
                tcs.TrySetResult();
            };
            timer.Start();
            return tcs.Task;
        }
    }
}
