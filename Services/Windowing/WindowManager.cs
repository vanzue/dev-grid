// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Services.Display;

namespace TopToolbar.Services.Windowing
{
    internal sealed class WindowManager : IDisposable
    {
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002;
        private const uint EventObjectHide = 0x8003;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint EventObjectNameChange = 0x800C;
        private const uint EventSystemForeground = 0x0003;
        private const uint EventFlagOutOfContext = 0x0000;
        private const uint EventFlagSkipOwnProcess = 0x0002;
        private const int ObjectIdWindow = 0;

        private readonly Dictionary<IntPtr, WindowInfo> _windows = new();
        private readonly List<IntPtr> _hookHandles = new();
        private readonly object _gate = new();
        private static readonly object HookOwnerGate = new();
        private static readonly Dictionary<IntPtr, WindowManager> HookOwners = new();
        private static readonly WinEventDelegate WinEventCallback = OnWinEventStatic;
        private readonly DisplayManager _displayManager;
        private bool _disposed;

        /// <summary>
        /// Raised when a window is destroyed.
        /// </summary>
        public event Action<IntPtr> WindowDestroyed;

        public event Action<WindowInfo> WindowCreated;

        public event Action<WindowInfo> WindowUpdated;

        public WindowManager(DisplayManager displayManager = null)
        {
            _displayManager = displayManager;
            RefreshAllWindows();
            StartListening();

            if (_displayManager != null)
            {
                _displayManager.MonitorsChanged += OnMonitorsChanged;
            }
        }

        public IReadOnlyList<WindowInfo> GetSnapshot()
        {
            lock (_gate)
            {
                return new List<WindowInfo>(_windows.Values);
            }
        }

        public bool TryGetWindow(IntPtr hwnd, out WindowInfo info)
        {
            lock (_gate)
            {
                return _windows.TryGetValue(hwnd, out info);
            }
        }

        public IReadOnlyList<WindowInfo> FindMatches(Func<WindowInfo, bool> predicate)
        {
            return FindMatches(predicate, 0);
        }

        public IReadOnlyList<WindowInfo> FindMatches(
            Func<WindowInfo, bool> predicate,
            uint expectedProcessId)
        {
            if (predicate == null)
            {
                return Array.Empty<WindowInfo>();
            }

            lock (_gate)
            {
                if (_windows.Count == 0)
                {
                    return Array.Empty<WindowInfo>();
                }

                var matches = new List<WindowInfo>();
                foreach (var window in _windows.Values)
                {
                    if (expectedProcessId != 0 && window.ProcessId != expectedProcessId)
                    {
                        continue;
                    }

                    if (predicate(window))
                    {
                        matches.Add(window);
                    }
                }

                return matches;
            }
        }

        public async Task<IReadOnlyList<WindowInfo>> WaitForWindowsAsync(
            Func<WindowInfo, bool> predicate,
            IReadOnlyCollection<IntPtr> knownHandles,
            uint expectedProcessId,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken
        )
        {
            if (predicate == null)
            {
                return Array.Empty<WindowInfo>();
            }

            var known =
                knownHandles != null && knownHandles.Count > 0
                    ? new HashSet<IntPtr>(knownHandles)
                    : new HashSet<IntPtr>();

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matches = FindMatches(predicate, expectedProcessId);
                if (matches.Count > 0)
                {
                    var newMatches = new List<WindowInfo>();
                    foreach (var match in matches)
                    {
                        if (!known.Contains(match.Handle))
                        {
                            newMatches.Add(match);
                        }
                    }

                    if (newMatches.Count > 0)
                    {
                        return newMatches;
                    }
                }

                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }

            return Array.Empty<WindowInfo>();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_displayManager != null)
            {
                _displayManager.MonitorsChanged -= OnMonitorsChanged;
            }

            List<IntPtr> handles;
            lock (_gate)
            {
                handles = new List<IntPtr>(_hookHandles);
                _hookHandles.Clear();
                _windows.Clear();
                _disposed = true;
            }

            lock (HookOwnerGate)
            {
                foreach (var handle in handles)
                {
                    HookOwners.Remove(handle);
                }
            }

            foreach (var handle in handles)
            {
                if (handle != IntPtr.Zero)
                {
                    _ = UnhookWinEvent(handle);
                }
            }

            GC.SuppressFinalize(this);
        }

        private void StartListening()
        {
            RegisterHook(EventSystemForeground);
            RegisterHook(EventObjectCreate);
            RegisterHook(EventObjectDestroy);
            RegisterHook(EventObjectShow);
            RegisterHook(EventObjectHide);
            RegisterHook(EventObjectLocationChange);
            RegisterHook(EventObjectNameChange);
        }

        private void RegisterHook(uint eventType)
        {
            var handle = SetWinEventHook(
                eventType,
                eventType,
                IntPtr.Zero,
                WinEventCallback,
                0,
                0,
                EventFlagOutOfContext | EventFlagSkipOwnProcess
            );
            if (handle != IntPtr.Zero)
            {
                _hookHandles.Add(handle);
                lock (HookOwnerGate)
                {
                    HookOwners[handle] = this;
                }
            }
        }

        private static void OnWinEventStatic(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            WindowManager owner = null;
            lock (HookOwnerGate)
            {
                HookOwners.TryGetValue(hWinEventHook, out owner);
            }

            owner?.OnWinEvent(
                hWinEventHook,
                eventType,
                hwnd,
                idObject,
                idChild,
                dwEventThread,
                dwmsEventTime);
        }

        private void RefreshAllWindows()
        {
            lock (_gate)
            {
                _windows.Clear();
            }

            _ = EnumWindows(
                (hwnd, _) =>
                {
                    RefreshWindow(hwnd);
                    return true;
                },
                IntPtr.Zero
            );
        }

        private void RefreshWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _disposed)
            {
                return;
            }

            if (!NativeWindowHelper.TryCreateWindowInfo(hwnd, out var info))
            {
                RemoveWindow(hwnd, notifyDestroyed: false);
                return;
            }

            info = AttachMonitorInfo(info);

            bool isNew = false;
            lock (_gate)
            {
                if (_windows.ContainsKey(hwnd))
                {
                    _windows[hwnd] = info;
                }
                else
                {
                    _windows.Add(hwnd, info);
                    isNew = true;
                }
            }

            try
            {
                if (isNew)
                {
                    WindowCreated?.Invoke(info);
                }
                else
                {
                    WindowUpdated?.Invoke(info);
                }
            }
            catch
            {
                // Suppress event handler exceptions.
            }
        }

        private void RefreshWindowLocation(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _disposed)
            {
                return;
            }

            WindowInfo existing;
            lock (_gate)
            {
                _windows.TryGetValue(hwnd, out existing);
            }

            if (existing == null)
            {
                RefreshWindow(hwnd);
                return;
            }

            if (!NativeWindowHelper.TryGetWindowBounds(hwnd, out var bounds))
            {
                RemoveWindow(hwnd, notifyDestroyed: !NativeWindowHelper.IsWindowHandleValid(hwnd));
                return;
            }

            var isVisible = existing.IsVisible;
            if (NativeWindowHelper.TryIsWindowVisible(hwnd, out var visible))
            {
                isVisible = visible;
            }

            var updated = existing.WithBounds(bounds, isVisible);
            updated = AttachMonitorInfo(updated);

            if (ReferenceEquals(updated, existing))
            {
                return;
            }

            lock (_gate)
            {
                _windows[hwnd] = updated;
            }

            try
            {
                WindowUpdated?.Invoke(updated);
            }
            catch
            {
            }
        }

        private WindowInfo AttachMonitorInfo(WindowInfo info)
        {
            if (info == null || _displayManager == null)
            {
                return info;
            }

            if (_displayManager.TryResolveMonitorForRect(
                info.Bounds.Left,
                info.Bounds.Top,
                info.Bounds.Right,
                info.Bounds.Bottom,
                out var monitor))
            {
                return info.WithMonitor(monitor.Id, monitor.Index);
            }

            return info.WithMonitor(string.Empty, 0);
        }

        private void UpdateMonitorAssignments()
        {
            if (_displayManager == null)
            {
                return;
            }

            List<WindowInfo> changed = null;
            lock (_gate)
            {
                foreach (var entry in _windows)
                {
                    var updated = AttachMonitorInfo(entry.Value);
                    if (!ReferenceEquals(updated, entry.Value))
                    {
                        _windows[entry.Key] = updated;
                        changed ??= new List<WindowInfo>();
                        changed.Add(updated);
                    }
                }
            }

            if (changed == null)
            {
                return;
            }

            foreach (var info in changed)
            {
                try
                {
                    WindowUpdated?.Invoke(info);
                }
                catch
                {
                }
            }
        }

        private void OnMonitorsChanged(object sender, EventArgs e)
        {
            UpdateMonitorAssignments();
        }

        private void RemoveWindow(IntPtr hwnd, bool notifyDestroyed)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            bool removed;
            lock (_gate)
            {
                removed = _windows.Remove(hwnd);
            }

            // Notify listeners that the window was destroyed
            if (removed && notifyDestroyed)
            {
                try
                {
                    WindowDestroyed?.Invoke(hwnd);
                }
                catch
                {
                    // Don't let subscriber exceptions crash the hook
                }
            }
        }

        private void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        )
        {
            if (_disposed)
            {
                return;
            }

            if (idObject != ObjectIdWindow || idChild != 0 || hwnd == IntPtr.Zero)
            {
                return;
            }

            switch (eventType)
            {
                case EventObjectDestroy:
                    RemoveWindow(hwnd, notifyDestroyed: true);
                    break;
                case EventObjectHide:
                    RemoveWindow(hwnd, notifyDestroyed: false);
                    break;
                case EventObjectShow:
                case EventObjectCreate:
                case EventObjectNameChange:
                case EventSystemForeground:
                    RefreshWindow(hwnd);
                    break;
                case EventObjectLocationChange:
                    RefreshWindowLocation(hwnd);
                    break;
                default:
                    break;
            }
        }

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        );

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags
        );

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    }
}
