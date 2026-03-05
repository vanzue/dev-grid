// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TopToolbar.Services.Windowing
{
    internal static partial class NativeWindowHelper
    {
        public static async Task SetWindowPlacementAsync(
            IntPtr hwnd,
            WindowPlacement position,
            bool maximize,
            bool minimize,
            bool waitForInputIdle = false,
            CancellationToken cancellationToken = default
        )
        {
            if (hwnd == IntPtr.Zero)
            {
                TopToolbar.Logging.AppLogger.LogInfo("SetWindowPlacement: skipped - hwnd is zero");
                return;
            }

            var hasPlacement = !position.IsEmpty;
            if (!hasPlacement && !maximize && !minimize)
            {
                TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: skipped - hwnd={hwnd}, empty placement and no window state change requested");
                return;
            }

            if (waitForInputIdle)
            {
                WaitForWindowInputIdle(hwnd);
            }

            if (!await EnsureWindowVisibleAsync(hwnd, cancellationToken).ConfigureAwait(false))
            {
                TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: EnsureWindowVisible failed for hwnd={hwnd}");
                return;
            }

            var placementApplied = true;
            if (hasPlacement)
            {
                placementApplied = SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    position.X,
                    position.Y,
                    position.Width,
                    position.Height,
                    SwpNoActivate | SwpNoZOrder
                );

                if (!placementApplied)
                {
                    TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: SetWindowPos failed for hwnd={hwnd}, but will still try ShowWindow");
                    // Don't return here - still try to minimize/maximize even if position failed
                    // This can happen with elevated windows (UIPI)
                }
            }

            TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: hwnd={hwnd}, minimize={minimize}, maximize={maximize}");
            if (minimize)
            {
                var result = ShowWindow(hwnd, SwShowMinimized);
                TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: ShowWindow(minimized) result={result}");
            }
            else if (maximize)
            {
                var result = ShowWindow(hwnd, SwShowMaximized);
                TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: ShowWindow(maximized) result={result}");
            }
            else
            {
                var shouldRestore = TryGetWindowPlacement(
                    hwnd,
                    out _,
                    out var isMinimized,
                    out var isMaximized)
                    && (isMinimized || isMaximized);
                if (shouldRestore)
                {
                    var result = ShowWindow(hwnd, SwShowNormal);
                    TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: ShowWindow(normal) result={result}");
                }
                else
                {
                    TopToolbar.Logging.AppLogger.LogInfo($"SetWindowPlacement: skipped ShowWindow(normal) for hwnd={hwnd} (already normal)");
                }

                if (hasPlacement && placementApplied)
                {
                    await VerifyWindowPlacementWithRetryAsync(hwnd, position, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task<bool> EnsureWindowVisibleAsync(
            IntPtr hwnd,
            CancellationToken cancellationToken)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            if (IsWindowVisible(hwnd))
            {
                return true;
            }

            _ = ShowWindow(hwnd, SwShowNormal);
            return await WaitForWindowVisibleAsync(hwnd, cancellationToken).ConfigureAwait(false);
        }

        private static async Task VerifyWindowPlacementWithRetryAsync(
            IntPtr hwnd,
            WindowPlacement position,
            CancellationToken cancellationToken)
        {
            if (hwnd == IntPtr.Zero || position.IsEmpty)
            {
                return;
            }

            for (var attempt = 0; attempt < WindowPlacementRetryAttempts; attempt++)
            {
                await Task.Delay(WindowPlacementRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);

                if (!IsWindow(hwnd))
                {
                    return;
                }

                if (IsWindowAtExpectedPlacement(hwnd, position))
                {
                    return;
                }

                _ = SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    position.X,
                    position.Y,
                    position.Width,
                    position.Height,
                    SwpNoActivate | SwpNoZOrder
                );
            }
        }

        private static bool IsWindowAtExpectedPlacement(
            IntPtr hwnd,
            WindowPlacement position
        )
        {
            var bounds = GetWindowBounds(hwnd);
            if (bounds.IsEmpty)
            {
                return false;
            }

            return Math.Abs(bounds.Left - position.X) <= WindowPlacementTolerancePixels
                && Math.Abs(bounds.Top - position.Y) <= WindowPlacementTolerancePixels
                && Math.Abs(bounds.Width - position.Width) <= WindowPlacementTolerancePixels
                && Math.Abs(bounds.Height - position.Height) <= WindowPlacementTolerancePixels;
        }

        private static async Task<bool> WaitForWindowVisibleAsync(
            IntPtr hwnd,
            CancellationToken cancellationToken)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            if (IsWindowVisible(hwnd))
            {
                return true;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < WindowVisibilityTimeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsWindow(hwnd))
                {
                    return false;
                }

                if (IsWindowVisible(hwnd))
                {
                    return true;
                }

                await Task.Delay(WindowVisibilityPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            return IsWindowVisible(hwnd);
        }

        private static void WaitForWindowInputIdle(IntPtr hwnd)
        {
            try
            {
                var threadId = GetWindowThreadProcessId(hwnd, out var processId);
                if (threadId == 0 || processId == 0)
                {
                    return;
                }

                using var process = Process.GetProcessById((int)processId);
                if (process.HasExited)
                {
                    return;
                }

                _ = process.WaitForInputIdle(WaitForInputIdleTimeoutMilliseconds);
            }
            catch (ArgumentException)
            {
                // Process exited before it became idle.
            }
            catch (InvalidOperationException)
            {
                // Process has no graphical interface to wait on.
            }
            catch (Win32Exception)
            {
                // Access denied or process information unavailable.
            }
        }

        public static bool TryGetWindowPlacement(
            IntPtr hwnd,
            out WindowBounds normalBounds,
            out bool isMinimized,
            out bool isMaximized
        )
        {
            normalBounds = default;
            isMinimized = false;
            isMaximized = false;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var placement = new NativeWindowPlacement
            {
                Length = Marshal.SizeOf<NativeWindowPlacement>(),
            };

            if (!GetWindowPlacement(hwnd, ref placement))
            {
                return false;
            }

            isMinimized = placement.ShowCmd == SwShowMinimized;
            isMaximized = placement.ShowCmd == SwShowMaximized;

            var rect = placement.NormalPosition;
            normalBounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }

        public static void MinimizeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Use async minimize to avoid cross-process UI-thread stalls during batch minimize.
            _ = ShowWindowAsync(hwnd, SwShowMinimized);
        }

        public static bool CanMinimizeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var style = GetWindowLongPtr(hwnd, GwlpStyle).ToInt64();
            if ((style & WsMinimize) == WsMinimize)
            {
                return false;
            }

            if ((style & WsMinimizeBox) != WsMinimizeBox)
            {
                return false;
            }

            return true;
        }

        public static bool TryActivateWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            try
            {
                _ = ShowWindow(hwnd, SwShowNormal);
                _ = BringWindowToTop(hwnd);
                _ = SetForegroundWindow(hwnd);

                if (GetForegroundWindow() == hwnd)
                {
                    return true;
                }

                if (TryPulseTopmostAndActivate(hwnd))
                {
                    return true;
                }

                var currentThread = GetCurrentThreadId();
                var foreground = GetForegroundWindow();
                var foregroundThread = foreground != IntPtr.Zero
                    ? GetWindowThreadProcessId(foreground, out _)
                    : 0;
                var targetThread = GetWindowThreadProcessId(hwnd, out _);

                var attachedToForeground = false;
                var attachedToTarget = false;

                try
                {
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                    {
                        attachedToForeground = AttachThreadInput(currentThread, foregroundThread, true);
                    }

                    if (targetThread != 0 && targetThread != currentThread)
                    {
                        attachedToTarget = AttachThreadInput(currentThread, targetThread, true);
                    }

                    _ = ShowWindow(hwnd, SwShowNormal);
                    _ = BringWindowToTop(hwnd);
                    _ = SetForegroundWindow(hwnd);

                    if (GetForegroundWindow() != hwnd)
                    {
                        _ = TryPulseTopmostAndActivate(hwnd);
                    }
                }
                finally
                {
                    if (attachedToTarget && targetThread != 0 && targetThread != currentThread)
                    {
                        _ = AttachThreadInput(currentThread, targetThread, false);
                    }

                    if (attachedToForeground && foregroundThread != 0 && foregroundThread != currentThread)
                    {
                        _ = AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }

                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryPulseTopmostAndActivate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            try
            {
                var flags = SwpNoMove | SwpNoSize | SwpShowWindow;
                _ = SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, flags);
                _ = SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, flags);
                _ = BringWindowToTop(hwnd);
                _ = SetForegroundWindow(hwnd);
                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
        }
    }
}
