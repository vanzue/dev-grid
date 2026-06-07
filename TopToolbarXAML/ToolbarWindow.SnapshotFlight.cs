// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services.Display;
using TopToolbar.Services.HotCorners;
using Windows.Foundation;
using Windows.Graphics;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        /// <summary>
        /// Identifies where a snapshot was triggered from, which decides where the camera "photo"
        /// animation flies to once the snapshot has been captured.
        /// </summary>
        private enum SnapshotFlightOrigin
        {
            ToolbarButton,
            Ring,
        }

        private CapturedBitmap _pendingFlightCapture;
        private RectInt32 _pendingFlightMonitorPx;
        private double _pendingFlightScale = 1.0;
        private bool _hasPendingFlight;
        private SnapshotFlightOrigin _pendingFlightOrigin;

        /// <summary>
        /// Captures the current screen at the moment a toolbar/ring snapshot begins, so the camera
        /// animation can replay exactly what the user saw before any prompts or window changes.
        /// </summary>
        private void BeginSnapshotFlight(SnapshotFlightOrigin origin)
        {
            _hasPendingFlight = false;
            _pendingFlightCapture = default;

            try
            {
                int pointX;
                int pointY;
                if (origin == SnapshotFlightOrigin.Ring && _radialSizePx > 0)
                {
                    pointX = _radialCenterScreenX;
                    pointY = _radialCenterScreenY;
                }
                else
                {
                    GetCursorPos(out var cursor);
                    pointX = cursor.X;
                    pointY = cursor.Y;
                }

                if (!TryCaptureMonitorForPoint(pointX, pointY, out var capture, out var monitorPx, out var scale))
                {
                    return;
                }

                _pendingFlightCapture = capture;
                _pendingFlightMonitorPx = monitorPx;
                _pendingFlightScale = scale;
                _pendingFlightOrigin = origin;
                _hasPendingFlight = true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"SnapshotFlight: capture failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Plays the camera animation toward the origin-specific target (snapshot button or ring center).
        /// Invoked only after a snapshot has been saved successfully.
        /// </summary>
        private async Task CompleteSnapshotFlightAsync()
        {
            if (!_hasPendingFlight || _photoFlight == null)
            {
                _hasPendingFlight = false;
                _pendingFlightCapture = default;
                return;
            }

            var capture = _pendingFlightCapture;
            var monitor = _pendingFlightMonitorPx;
            var scale = _pendingFlightScale;
            var origin = _pendingFlightOrigin;

            _hasPendingFlight = false;
            _pendingFlightCapture = default;

            await EnqueueAsync(async () =>
            {
                try
                {
                    RectInt32 target;
                    if (origin == SnapshotFlightOrigin.Ring)
                    {
                        target = BuildRingTargetRect();
                    }
                    else
                    {
                        ShowToolbar();
                        if (!TryGetElementScreenRect(SnapshotButton, out target))
                        {
                            return;
                        }
                    }

                    await _photoFlight.PlayAsync(capture, monitor, target, scale).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"SnapshotFlight: flight failed - {ex.GetType().Name}: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        private void DiscardPendingSnapshotFlight()
        {
            _hasPendingFlight = false;
            _pendingFlightCapture = default;
        }

        private RectInt32 BuildRingTargetRect()
        {
            var side = Math.Max(48, (int)Math.Round(_radialSizePx * 0.18));
            var x = _radialCenterScreenX - (side / 2);
            var y = _radialCenterScreenY - (side / 2);
            return new RectInt32(x, y, side, side);
        }

        /// <summary>
        /// Captures the full physical-pixel screenshot of the monitor that contains the given screen point.
        /// </summary>
        private bool TryCaptureMonitorForPoint(int screenX, int screenY, out CapturedBitmap capture, out RectInt32 monitorPx, out double scale)
        {
            capture = default;
            monitorPx = default;
            scale = 1.0;

            var manager = _hotCornerDisplayManager;
            if (manager == null)
            {
                return false;
            }

            if (!manager.TryResolveMonitorForRect(screenX, screenY, screenX + 1, screenY + 1, out var monitor) || monitor == null)
            {
                return false;
            }

            var bounds = monitor.Bounds;
            if (bounds.IsEmpty)
            {
                return false;
            }

            var grabbed = ScreenCaptureService.Capture(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            if (!grabbed.IsValid)
            {
                return false;
            }

            capture = grabbed;
            monitorPx = new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            scale = monitor.Dpi > 0 ? monitor.Dpi / 96.0 : 1.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            return true;
        }

        /// <summary>
        /// Resolves the on-screen (physical-pixel) rectangle of a framework element living in this window.
        /// </summary>
        private bool TryGetElementScreenRect(FrameworkElement element, out RectInt32 rect)
        {
            rect = default;
            if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            if (element.XamlRoot?.Content is not UIElement contentRoot)
            {
                return false;
            }

            try
            {
                var transform = element.TransformToVisual(contentRoot);
                var topLeft = transform.TransformPoint(new Point(0, 0));
                var scale = element.XamlRoot?.RasterizationScale ?? 1.0;
                if (scale <= 0)
                {
                    scale = 1.0;
                }

                var pos = AppWindow.Position;
                var x = (int)Math.Round(pos.X + (topLeft.X * scale));
                var y = (int)Math.Round(pos.Y + (topLeft.Y * scale));
                var w = (int)Math.Round(element.ActualWidth * scale);
                var h = (int)Math.Round(element.ActualHeight * scale);
                if (w <= 0 || h <= 0)
                {
                    return false;
                }

                rect = new RectInt32(x, y, w, h);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"SnapshotFlight: element rect failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds a small target rectangle anchored at the triggered hot corner of its monitor.
        /// </summary>
        private static RectInt32 BuildCornerTargetRect(HotCorner corner, RectInt32 monitorPx)
        {
            var size = (int)Math.Clamp(Math.Min(monitorPx.Width, monitorPx.Height) * 0.08, 80, 240);
            var right = monitorPx.X + monitorPx.Width - size;
            var bottom = monitorPx.Y + monitorPx.Height - size;

            var (x, y) = corner switch
            {
                HotCorner.TopLeft => (monitorPx.X, monitorPx.Y),
                HotCorner.TopRight => (right, monitorPx.Y),
                HotCorner.BottomLeft => (monitorPx.X, bottom),
                _ => (right, bottom),
            };

            return new RectInt32(x, y, size, size);
        }
    }
}
