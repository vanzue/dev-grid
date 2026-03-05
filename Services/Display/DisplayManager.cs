// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TopToolbar.Services.Display
{
    internal sealed class DisplayManager : IDisposable
    {
        private const int RefreshIntervalMilliseconds = 1000;

        private readonly object _gate = new();
        private readonly Timer _refreshTimer;
        private List<DisplayMonitor> _snapshot = new();
        private int _refreshInProgress;
        private bool _disposed;

        public event EventHandler MonitorsChanged;

        public DisplayManager()
        {
            _snapshot = CaptureMonitors();
            _refreshTimer = new Timer(
                _ => RefreshMonitors(),
                null,
                RefreshIntervalMilliseconds,
                RefreshIntervalMilliseconds);
        }

        public IReadOnlyList<DisplayMonitor> GetSnapshot()
        {
            lock (_gate)
            {
                return new List<DisplayMonitor>(_snapshot);
            }
        }

        public bool TryResolveMonitorForRect(
            int left,
            int top,
            int right,
            int bottom,
            out DisplayMonitor monitor)
        {
            monitor = null;

            var snapshot = GetSnapshot();
            if (snapshot.Count == 0)
            {
                return false;
            }

            var centerX = left + ((right - left) / 2);
            var centerY = top + ((bottom - top) / 2);

            DisplayMonitor best = null;
            long bestArea = -1;

            foreach (var entry in snapshot)
            {
                var rect = entry.Bounds;
                if (centerX >= rect.Left && centerX < rect.Right
                    && centerY >= rect.Top && centerY < rect.Bottom)
                {
                    monitor = entry;
                    return true;
                }

                var area = CalculateIntersectionArea(left, top, right, bottom, rect);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = entry;
                }
            }

            if (best != null)
            {
                monitor = best;
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _refreshTimer?.Dispose();
        }

        private void RefreshMonitors()
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
            {
                return;
            }

            var updated = CaptureMonitors();
            bool changed;

            lock (_gate)
            {
                changed = HasSnapshotChanged(_snapshot, updated);
                if (changed)
                {
                    _snapshot = updated;
                }
            }

            try
            {
                if (changed)
                {
                    try
                    {
                        MonitorsChanged?.Invoke(this, EventArgs.Empty);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInProgress, 0);
            }
        }

        private static bool HasSnapshotChanged(
            IReadOnlyList<DisplayMonitor> oldSnapshot,
            IReadOnlyList<DisplayMonitor> newSnapshot)
        {
            if (oldSnapshot.Count != newSnapshot.Count)
            {
                return true;
            }

            for (int i = 0; i < oldSnapshot.Count; i++)
            {
                var oldEntry = oldSnapshot[i];
                var newEntry = newSnapshot[i];

                if (!string.Equals(oldEntry.Id, newEntry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.Equals(oldEntry.InstanceId, newEntry.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (oldEntry.Dpi != newEntry.Dpi)
                {
                    return true;
                }

                if (!oldEntry.DpiAwareRect.Equals(newEntry.DpiAwareRect)
                    || !oldEntry.DpiUnawareRect.Equals(newEntry.DpiUnawareRect)
                    || !oldEntry.DpiAwareWorkRect.Equals(newEntry.DpiAwareWorkRect)
                    || !oldEntry.DpiUnawareWorkRect.Equals(newEntry.DpiUnawareWorkRect))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<DisplayMonitor> CaptureMonitors()
        {
            var snapshots = new List<DisplayMonitor>();

            try
            {
                int index = 0;
                MonitorEnumProc callback = (
                    IntPtr hMonitor,
                    IntPtr hdcMonitor,
                    ref NativeMonitorRect rect,
                    IntPtr data
                ) =>
                {
                    try
                    {
                        var info = new MONITORINFOEX
                        {
                            CbSize = Marshal.SizeOf<MONITORINFOEX>(),
                            SzDevice = string.Empty,
                        };

                        if (!GetMonitorInfo(hMonitor, ref info))
                        {
                            return true;
                        }

                        uint dpiX = 96;
                        uint dpiY = 96;
                        try
                        {
                            var hr = GetDpiForMonitor(
                                hMonitor,
                                MonitorDpiType.EffectiveDpi,
                                out dpiX,
                                out dpiY
                            );
                            if (hr != 0)
                            {
                                dpiX = dpiY = 96;
                            }
                        }
                        catch
                        {
                            dpiX = dpiY = 96;
                        }

                        var bounds = new DisplayRect(
                            info.RcMonitor.Left,
                            info.RcMonitor.Top,
                            info.RcMonitor.Right - info.RcMonitor.Left,
                            info.RcMonitor.Bottom - info.RcMonitor.Top
                        );

                        var workArea = new DisplayRect(
                            info.RcWork.Left,
                            info.RcWork.Top,
                            info.RcWork.Right - info.RcWork.Left,
                            info.RcWork.Bottom - info.RcWork.Top
                        );
                        if (workArea.IsEmpty)
                        {
                            workArea = bounds;
                        }

                        var scale = dpiX > 0 ? (double)dpiX / 96.0 : 1.0;
                        if (scale <= 0)
                        {
                            scale = 1.0;
                        }

                        var dpiUnawareRect = ToDpiUnawareRect(bounds, scale);
                        var dpiUnawareWorkRect = ToDpiUnawareRect(workArea, scale);

                        var id = string.IsNullOrWhiteSpace(info.SzDevice)
                            ? $"DISPLAY{index}"
                            : info.SzDevice.Trim();
                        var instanceId = ResolveMonitorInstanceId(info.SzDevice, id);

                        var monitor = new DisplayMonitor(
                            id,
                            instanceId,
                            index,
                            (int)dpiX,
                            bounds,
                            dpiUnawareRect,
                            workArea,
                            dpiUnawareWorkRect);

                        snapshots.Add(monitor);
                        index++;
                    }
                    catch
                    {
                    }

                    return true;
                };

                _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            }
            catch
            {
            }

            return snapshots;
        }

        private static long CalculateIntersectionArea(
            int left,
            int top,
            int right,
            int bottom,
            DisplayRect monitor)
        {
            var areaLeft = Math.Max(left, monitor.Left);
            var areaTop = Math.Max(top, monitor.Top);
            var areaRight = Math.Min(right, monitor.Right);
            var areaBottom = Math.Min(bottom, monitor.Bottom);

            var width = areaRight - areaLeft;
            var height = areaBottom - areaTop;
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (long)width * height;
        }

        private static DisplayRect ToDpiUnawareRect(DisplayRect rect, double scale)
        {
            if (rect.IsEmpty)
            {
                return rect;
            }

            return new DisplayRect(
                (int)Math.Round(rect.Left / scale),
                (int)Math.Round(rect.Top / scale),
                (int)Math.Round(rect.Width / scale),
                (int)Math.Round(rect.Height / scale));
        }

        private static string ResolveMonitorInstanceId(string monitorDeviceName, string fallbackId)
        {
            if (string.IsNullOrWhiteSpace(monitorDeviceName))
            {
                return fallbackId ?? string.Empty;
            }

            try
            {
                var device = new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (EnumDisplayDevices(monitorDeviceName, 0, ref device, 0)
                    && !string.IsNullOrWhiteSpace(device.DeviceID))
                {
                    return device.DeviceID.Trim();
                }
            }
            catch
            {
            }

            return fallbackId ?? string.Empty;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData
        );

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(
            string lpDevice,
            uint iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice,
            uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(
            IntPtr hmonitor,
            MonitorDpiType dpiType,
            out uint dpiX,
            out uint dpiY
        );

        private delegate bool MonitorEnumProc(
            IntPtr hMonitor,
            IntPtr hdcMonitor,
            ref NativeMonitorRect lprcMonitor,
            IntPtr dwData
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorRect
        {
            public int Left;

            public int Top;

            public int Right;

            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int CbSize;

            public NativeMonitorRect RcMonitor;

            public NativeMonitorRect RcWork;

            public uint DwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string SzDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;

            public int StateFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private enum MonitorDpiType
        {
            EffectiveDpi = 0,
            AngularDpi = 1,
            RawDpi = 2,
        }
    }
}
