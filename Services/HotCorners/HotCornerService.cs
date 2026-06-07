// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services.Display;
using Windows.Graphics;

namespace TopToolbar.Services.HotCorners
{
    internal readonly struct HotCornerHoverState
    {
        public HotCornerHoverState(bool active, HotCorner corner, DisplayRect monitorBounds, double scale, double progress)
        {
            Active = active;
            Corner = corner;
            MonitorBounds = monitorBounds;
            Scale = scale;
            Progress = progress;
        }

        public bool Active { get; }

        public HotCorner Corner { get; }

        public DisplayRect MonitorBounds { get; }

        public double Scale { get; }

        public double Progress { get; }
    }

    internal readonly struct HotCornerActionContext
    {
        public HotCornerActionContext(string actionId, HotCorner corner, DisplayRect monitorBounds, double scale)
        {
            ActionId = actionId;
            Corner = corner;
            MonitorBounds = monitorBounds;
            Scale = scale;
        }

        public string ActionId { get; }

        public HotCorner Corner { get; }

        public DisplayRect MonitorBounds { get; }

        public double Scale { get; }
    }

    internal sealed class HotCornerService : IDisposable
    {
        private const int PollIntervalMs = 40;

        private readonly DispatcherQueue _dispatcher;
        private readonly DisplayManager _displayManager;
        private readonly DispatcherQueueTimer _timer;

        private HotCornersConfig _config = new();
        private bool _hasActive;
        private HotCorner _activeCorner;
        private DisplayRect _activeBounds;
        private double _activeScale = 1.0;
        private long _enterTimestamp;
        private bool _latched;
        private double _lastProgress = -1;
        private bool _disposed;
        private DateTime _lastResolveLogUtc = DateTime.MinValue;
        private string _lastResolveLogSignature = string.Empty;

        public HotCornerService(DispatcherQueue dispatcher, DisplayManager displayManager)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _displayManager = displayManager ?? throw new ArgumentNullException(nameof(displayManager));

            _timer = _dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(PollIntervalMs);
            _timer.IsRepeating = true;
            _timer.Tick += (_, __) => Poll();
        }

        public event Action<HotCornerHoverState> HoverChanged;

        public event Action<HotCornerActionContext> ActionTriggered;

        public void ApplyConfig(HotCornersConfig config)
        {
            _config = config ?? new HotCornersConfig();
            _config.Actions ??= new System.Collections.Generic.Dictionary<HotCorner, string>();

            if (_config.Enabled && HasAnyMappedAction())
            {
                if (!_timer.IsRunning)
                {
                    _timer.Start();
                }
            }
            else
            {
                _timer.Stop();
                ClearActive();
            }
        }

        private bool HasAnyMappedAction()
        {
            foreach (var pair in _config.Actions)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value) &&
                    !string.Equals(pair.Value, HotCornerActions.None, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Poll()
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            if (!GetCursorPos(out var point))
            {
                ClearActive();
                return;
            }

            var candidate = ResolveCorner(point.X, point.Y, out var bounds, out var scale);

            if (candidate == null)
            {
                ClearActive();
                return;
            }

            if (_config.DisableOnFullScreen && IsForegroundFullScreen(bounds))
            {
                ClearActive();
                return;
            }

            var corner = candidate.Value;

            if (_hasActive && _activeCorner == corner)
            {
                var elapsed = ElapsedMs(_enterTimestamp);
                var dwell = Math.Max(1, _config.DwellMilliseconds);
                var progress = Math.Clamp(elapsed / (double)dwell, 0.0, 1.0);

                if (!_latched && elapsed >= dwell)
                {
                    _latched = true;
                    progress = 1.0;
                    RaiseHover(true, corner, bounds, scale, progress);
                    Trigger(corner);
                    return;
                }

                RaiseHover(true, corner, bounds, scale, progress);
                return;
            }

            // New corner entered.
            _hasActive = true;
            _activeCorner = corner;
            _activeBounds = bounds;
            _activeScale = scale;
            _enterTimestamp = NowMs();
            _latched = false;
            _lastProgress = -1;
            RaiseHover(true, corner, bounds, scale, 0.0);
        }

        private HotCorner? ResolveCorner(int x, int y, out DisplayRect bounds, out double scale)
        {
            bounds = default;
            scale = 1.0;
            var zone = Math.Max(1, _config.HotZonePx);
            if (TryResolveDisplayAreaCorner(x, y, zone, out bounds, out scale, out var displayAreaCorner))
            {
                return displayAreaCorner;
            }

            var monitors = _displayManager.GetSnapshot();

            foreach (var monitor in monitors)
            {
                var rect = monitor.Bounds; // DPI-aware, physical pixels
                if (rect.IsEmpty)
                {
                    continue;
                }

                if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
                {
                    continue;
                }

                var nearLeft = x <= rect.Left + zone;
                var nearRight = x >= rect.Right - zone;
                var nearTop = y <= rect.Top + zone;
                var nearBottom = y >= rect.Bottom - zone;

                HotCorner? corner = null;
                if (nearTop && nearLeft)
                {
                    corner = HotCorner.TopLeft;
                }
                else if (nearTop && nearRight)
                {
                    corner = HotCorner.TopRight;
                }
                else if (nearBottom && nearLeft)
                {
                    corner = HotCorner.BottomLeft;
                }
                else if (nearBottom && nearRight)
                {
                    corner = HotCorner.BottomRight;
                }

                if (corner == null)
                {
                    return null;
                }

                if (!IsMapped(corner.Value))
                {
                    return null;
                }

                bounds = rect;
                scale = monitor.Dpi > 0 ? monitor.Dpi / 96.0 : 1.0;
                LogResolvedCorner(x, y, zone, monitor, corner.Value, scale, nearLeft, nearRight, nearTop, nearBottom);
                return corner;
            }

            return null;
        }

        private bool TryResolveDisplayAreaCorner(int x, int y, int zone, out DisplayRect bounds, out double scale, out HotCorner? corner)
        {
            bounds = default;
            scale = 1.0;
            corner = null;

            try
            {
                var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.None);
                if (area == null)
                {
                    return false;
                }

                var outer = area.OuterBounds;
                if (outer.Width <= 0 || outer.Height <= 0)
                {
                    return false;
                }

                var rect = new DisplayRect(outer.X, outer.Y, outer.Width, outer.Height);
                corner = ResolveCornerInBounds(x, y, zone, rect, out var nearLeft, out var nearRight, out var nearTop, out var nearBottom);
                if (corner == null)
                {
                    return true;
                }

                if (!IsMapped(corner.Value))
                {
                    corner = null;
                    return true;
                }

                bounds = rect;
                LogResolvedDisplayAreaCorner(x, y, zone, area, rect, corner.Value, nearLeft, nearRight, nearTop, nearBottom);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"HotCornerResolve: DisplayArea resolve failed - {ex.Message}");
                return false;
            }
        }

        private static HotCorner? ResolveCornerInBounds(
            int x,
            int y,
            int zone,
            DisplayRect rect,
            out bool nearLeft,
            out bool nearRight,
            out bool nearTop,
            out bool nearBottom)
        {
            nearLeft = x <= rect.Left + zone;
            nearRight = x >= rect.Right - zone;
            nearTop = y <= rect.Top + zone;
            nearBottom = y >= rect.Bottom - zone;

            if (nearTop && nearLeft)
            {
                return HotCorner.TopLeft;
            }

            if (nearTop && nearRight)
            {
                return HotCorner.TopRight;
            }

            if (nearBottom && nearLeft)
            {
                return HotCorner.BottomLeft;
            }

            if (nearBottom && nearRight)
            {
                return HotCorner.BottomRight;
            }

            return null;
        }

        private void LogResolvedCorner(
            int cursorX,
            int cursorY,
            int zone,
            DisplayMonitor monitor,
            HotCorner corner,
            double scale,
            bool nearLeft,
            bool nearRight,
            bool nearTop,
            bool nearBottom)
        {
            var rect = monitor.Bounds;
            var signature =
                $"{corner}|cursor={cursorX},{cursorY}|monitor={monitor.Index}:{rect.Left},{rect.Top},{rect.Width},{rect.Height}|zone={zone}";
            var now = DateTime.UtcNow;
            if (string.Equals(signature, _lastResolveLogSignature, StringComparison.Ordinal) &&
                (now - _lastResolveLogUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            _lastResolveLogSignature = signature;
            _lastResolveLogUtc = now;

            AppLogger.LogInfo(
                $"HotCornerResolve: cursor=({cursorX},{cursorY}), zone={zone}, corner={corner}, monitor[{monitor.Index}] id='{monitor.Id}', dpi={monitor.Dpi}, scale={scale:F3}, bounds=({rect.Left},{rect.Top},{rect.Width},{rect.Height}) rb=({rect.Right},{rect.Bottom}), near=(left:{nearLeft},right:{nearRight},top:{nearTop},bottom:{nearBottom}).");
        }

        private void LogResolvedDisplayAreaCorner(
            int cursorX,
            int cursorY,
            int zone,
            DisplayArea area,
            DisplayRect rect,
            HotCorner corner,
            bool nearLeft,
            bool nearRight,
            bool nearTop,
            bool nearBottom)
        {
            var signature =
                $"displayArea|{corner}|cursor={cursorX},{cursorY}|area={area.DisplayId.Value}:{rect.Left},{rect.Top},{rect.Width},{rect.Height}|zone={zone}";
            var now = DateTime.UtcNow;
            if (string.Equals(signature, _lastResolveLogSignature, StringComparison.Ordinal) &&
                (now - _lastResolveLogUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            _lastResolveLogSignature = signature;
            _lastResolveLogUtc = now;

            AppLogger.LogInfo(
                $"HotCornerResolve: source=DisplayArea, cursor=({cursorX},{cursorY}), zone={zone}, corner={corner}, displayId={area.DisplayId.Value}, bounds=({rect.Left},{rect.Top},{rect.Width},{rect.Height}) rb=({rect.Right},{rect.Bottom}), near=(left:{nearLeft},right:{nearRight},top:{nearTop},bottom:{nearBottom}).");
        }

        private bool IsMapped(HotCorner corner)
        {
            return _config.Actions.TryGetValue(corner, out var action) &&
                   !string.IsNullOrWhiteSpace(action) &&
                   !string.Equals(action, HotCornerActions.None, StringComparison.OrdinalIgnoreCase);
        }

        private void Trigger(HotCorner corner)
        {
            if (!_config.Actions.TryGetValue(corner, out var actionId) || string.IsNullOrWhiteSpace(actionId))
            {
                return;
            }

            AppLogger.LogInfo($"HotCorner: triggered corner={corner}, action='{actionId}'.");
            ActionTriggered?.Invoke(new HotCornerActionContext(actionId, corner, _activeBounds, _activeScale));
        }

        private void ClearActive()
        {
            if (!_hasActive)
            {
                return;
            }

            var corner = _activeCorner;
            var bounds = _activeBounds;
            var scale = _activeScale;
            _hasActive = false;
            _latched = false;
            _lastProgress = -1;
            RaiseHover(false, corner, bounds, scale, 0.0, force: true);
        }

        private void RaiseHover(bool active, HotCorner corner, DisplayRect bounds, double scale, double progress, bool force = false)
        {
            if (!force && active && Math.Abs(progress - _lastProgress) < 0.01)
            {
                return;
            }

            _lastProgress = active ? progress : -1;
            HoverChanged?.Invoke(new HotCornerHoverState(active, corner, bounds, scale, progress));
        }

        private static bool IsForegroundFullScreen(DisplayRect monitor)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var shell = GetShellWindow();
            var desktop = GetDesktopWindow();
            if (hwnd == shell || hwnd == desktop)
            {
                return false;
            }

            if (!GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            const int tolerance = 2;
            return rect.Left <= monitor.Left + tolerance &&
                   rect.Top <= monitor.Top + tolerance &&
                   rect.Right >= monitor.Right - tolerance &&
                   rect.Bottom >= monitor.Bottom - tolerance;
        }

        private static long NowMs() => Environment.TickCount64;

        private static long ElapsedMs(long start) => Environment.TickCount64 - start;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    }
}
