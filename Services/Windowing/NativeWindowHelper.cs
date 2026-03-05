// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TopToolbar.Services.Windowing
{
    internal static partial class NativeWindowHelper
    {
        private static readonly Lazy<IVirtualDesktopManager> VirtualDesktopManagerInstance = new(
            () =>
            {
                try
                {
                    return (IVirtualDesktopManager)new VirtualDesktopManager();
                }
                catch (COMException ex)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: failed to initialize IVirtualDesktopManager, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: failed to initialize IVirtualDesktopManager, type={ex.GetType().Name}, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                    return null;
                }
            });

        public static bool IsVirtualDesktopManagerAvailable()
        {
            try
            {
                return VirtualDesktopManagerInstance.Value != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsVirtualDesktopApiReliable()
        {
            if (!IsVirtualDesktopManagerAvailable())
            {
                return false;
            }

            return TryGetCurrentVirtualDesktopId(out _);
        }

        public static bool TryGetForegroundWindowHandle(out IntPtr handle)
        {
            handle = IntPtr.Zero;

            try
            {
                handle = GetForegroundWindow();
                return handle != IntPtr.Zero;
            }
            catch
            {
                handle = IntPtr.Zero;
                return false;
            }
        }

        public static bool TryGetCurrentVirtualDesktopId(out Guid desktopId)
        {
            desktopId = Guid.Empty;

            try
            {
                var manager = VirtualDesktopManagerInstance.Value;
                if (manager == null)
                {
                    return false;
                }

                return TryGetCurrentVirtualDesktopId(manager, out desktopId);
            }
            catch
            {
                return false;
            }
        }

        public static IReadOnlyList<IntPtr> EnumerateProcessWindows(int processId)
        {
            if (processId <= 0)
            {
                return Array.Empty<IntPtr>();
            }

            var windows = new List<IntPtr>();
            _ = EnumWindows(
                (hwnd, _) =>
                {
                    if (!IsTopLevelWindow(hwnd))
                    {
                        return true;
                    }

                    var threadId = GetWindowThreadProcessId(hwnd, out var windowProcessId);
                    if (threadId == 0 || windowProcessId == 0)
                    {
                        return true;
                    }

                    if ((int)windowProcessId == processId)
                    {
                        windows.Add(hwnd);
                    }

                    return true;
                },
                IntPtr.Zero
            );

            return windows;
        }

        public static bool TryCreateWindowInfo(IntPtr hwnd, out WindowInfo info)
        {
            info = null;

            if (!IsTopLevelWindow(hwnd))
            {
                return false;
            }

            var threadId = GetWindowThreadProcessId(hwnd, out var processId);
            if (threadId == 0 || processId == 0)
            {
                return false;
            }

            var title = GetWindowTitle(hwnd);
            var bounds = GetWindowBounds(hwnd);
            var isVisible = IsWindowVisible(hwnd);
            var processPath = TryGetProcessPath(processId);
            var processFileName = string.IsNullOrWhiteSpace(processPath)
                ? string.Empty
                : Path.GetFileName(processPath);
            var processName = string.IsNullOrWhiteSpace(processFileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(processFileName);
            var packageFullName = TryGetPackageFullName(processId);
            var appUserModelId = TryGetAppUserModelId(hwnd);
            var className = GetWindowClassName(hwnd);

            info = new WindowInfo(
                hwnd,
                processId,
                processPath,
                processFileName,
                processName,
                packageFullName,
                title,
                appUserModelId,
                isVisible,
                bounds,
                className,
                string.Empty,
                0
            );
            return true;
        }

        public static bool TryGetWindowBounds(IntPtr hwnd, out WindowBounds bounds)
        {
            bounds = default;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (GetWindowRect(hwnd, out var rect))
            {
                bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                return true;
            }

            return false;
        }

        public static bool TryIsWindowVisible(IntPtr hwnd, out bool isVisible)
        {
            isVisible = false;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                isVisible = IsWindowVisible(hwnd);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasToolWindowStyle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var exStyle = GetWindowLongPtr(hwnd, GwlpExStyle).ToInt64();
            return (exStyle & WsExToolWindow) == WsExToolWindow;
        }

        public static bool IsTopLevelWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow())
            {
                return false;
            }

            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return false;
            }

            var style = GetWindowLongPtr(hwnd, GwlpStyle).ToInt64();
            if ((style & WsChild) == WsChild)
            {
                return false;
            }

            if ((style & WsVisible) != WsVisible)
            {
                return false;
            }

            if ((style & WsDisabled) == WsDisabled)
            {
                return false;
            }

            return IsWindow(hwnd);
        }

        private static string TryGetProcessPath(uint processId)
        {
            if (processId == 0)
            {
                return string.Empty;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessAccess.QueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero)
                {
                    return string.Empty;
                }

                var capacity = 1024u;
                var builder = new StringBuilder((int)capacity);
                if (QueryFullProcessImageName(handle, 0, builder, ref capacity))
                {
                    return builder.ToString();
                }
            }
            catch { }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    _ = CloseHandle(handle);
                }
            }

            return string.Empty;
        }

        private static WindowBounds GetWindowBounds(IntPtr hwnd)
        {
            if (GetWindowRect(hwnd, out var rect))
            {
                return new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }

            return new WindowBounds(0, 0, 0, 0);
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(length + 1);
                _ = GetWindowText(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(256);
            var read = GetClassName(hwnd, builder, builder.Capacity);
            if (read <= 0)
            {
                return string.Empty;
            }

            return builder.ToString();
        }

        private static string TryGetAppUserModelId(IntPtr hwnd)
        {
            IntPtr propertyStorePtr = IntPtr.Zero;
            try
            {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out propertyStorePtr);
                if (hr != 0 || propertyStorePtr == IntPtr.Zero)
                {
                    return string.Empty;
                }

                var key = new PropertyKey
                {
                    FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                    PropertyId = 5,
                };

                var value = new PropVariant();
                hr = PropertyStoreGetValue(propertyStorePtr, ref key, ref value);
                if (hr != 0)
                {
                    return string.Empty;
                }

                try
                {
                    return ExtractString(value);
                }
                finally
                {
                    _ = PropVariantClear(ref value);
                }
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (propertyStorePtr != IntPtr.Zero)
                {
                    _ = Marshal.Release(propertyStorePtr);
                }
            }
        }

        // IPropertyStore::GetValue is at vtable index 5 (after IUnknown's 3 methods + GetCount + GetAt)
        private static int PropertyStoreGetValue(IntPtr propertyStore, ref PropertyKey key, ref PropVariant value)
        {
            // Get the vtable pointer
            var vtable = Marshal.ReadIntPtr(propertyStore);
            // GetValue is at index 5 in the vtable (0=QueryInterface, 1=AddRef, 2=Release, 3=GetCount, 4=GetAt, 5=GetValue)
            var getValuePtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var getValueDelegate = Marshal.GetDelegateForFunctionPointer<PropertyStoreGetValueDelegate>(getValuePtr);
            return getValueDelegate(propertyStore, ref key, ref value);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropertyStoreGetValueDelegate(IntPtr thisPtr, ref PropertyKey key, ref PropVariant value);

        private static string ExtractString(PropVariant value)
        {
            if (value.ValueType == ValueTypeLpwstr && value.PointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty;
            }

            return string.Empty;
        }

        private static string TryGetPackageFullName(uint processId)
        {
            if (processId == 0)
            {
                return string.Empty;
            }

            var handle = OpenProcess(ProcessAccess.QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                // First call to get the required length.
                int length = 0;
                var hr = GetPackageFullName(handle, ref length, null);
                const int ErrorInsufficientBuffer = 122;
                const int AppModelErrorNoPackage = 15700;
                if (hr == AppModelErrorNoPackage)
                {
                    return string.Empty;
                }

                if (hr != ErrorInsufficientBuffer || length <= 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(length);
                hr = GetPackageFullName(handle, ref length, builder);
                if (hr == 0)
                {
                    return builder.ToString();
                }
            }
            catch
            {
            }
            finally
            {
                _ = CloseHandle(handle);
            }

            return string.Empty;
        }

        public static bool TryIsWindowOnCurrentVirtualDesktop(
            IntPtr hwnd,
            out bool isOnCurrentDesktop)
        {
            isOnCurrentDesktop = true;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var manager = VirtualDesktopManagerInstance.Value;
                if (manager == null)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: IsWindowOnCurrentVirtualDesktop unavailable (manager null), hwnd={FormatHwnd(hwnd)}");
                    return false;
                }

                var hr = manager.IsWindowOnCurrentVirtualDesktop(hwnd, out isOnCurrentDesktop);
                if (hr != 0)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: IsWindowOnCurrentVirtualDesktop failed, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(hr)}");
                }

                return hr == 0;
            }
            catch (COMException ex)
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: IsWindowOnCurrentVirtualDesktop exception, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                return false;
            }
            catch
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: IsWindowOnCurrentVirtualDesktop exception, hwnd={FormatHwnd(hwnd)}");
                return false;
            }
        }

        public static bool TryGetWindowVirtualDesktopId(IntPtr hwnd, out Guid desktopId)
        {
            desktopId = Guid.Empty;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var manager = VirtualDesktopManagerInstance.Value;
                if (manager == null)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: GetWindowDesktopId unavailable (manager null), hwnd={FormatHwnd(hwnd)}");
                    return false;
                }

                var hr = manager.GetWindowDesktopId(hwnd, out desktopId);
                if (hr != 0)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: GetWindowDesktopId failed, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(hr)}");
                }
                else if (desktopId == Guid.Empty)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: GetWindowDesktopId returned empty desktop id, hwnd={FormatHwnd(hwnd)}");
                }

                return hr == 0 && desktopId != Guid.Empty;
            }
            catch (COMException ex)
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: GetWindowDesktopId exception, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                return false;
            }
            catch
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: GetWindowDesktopId exception, hwnd={FormatHwnd(hwnd)}");
                return false;
            }
        }

        public static bool TryMoveWindowToCurrentVirtualDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var manager = VirtualDesktopManagerInstance.Value;
                if (manager == null)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: MoveWindowToDesktop unavailable (manager null), hwnd={FormatHwnd(hwnd)}");
                    return false;
                }

                var onCurrentDesktopHr = manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var isOnCurrentDesktop);
                if (onCurrentDesktopHr != 0)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: pre-check IsWindowOnCurrentVirtualDesktop failed before move, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(onCurrentDesktopHr)}");
                    return false;
                }

                if (onCurrentDesktopHr == 0 && isOnCurrentDesktop)
                {
                    return true;
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (!TryGetCurrentVirtualDesktopId(manager, out var currentDesktopId))
                    {
                        TopToolbar.Logging.AppLogger.LogInfo(
                            $"VirtualDesktop: could not resolve current desktop id before move, hwnd={FormatHwnd(hwnd)}, attempt={attempt + 1}");
                        Thread.Sleep(40);
                        continue;
                    }

                    var hr = manager.MoveWindowToDesktop(hwnd, ref currentDesktopId);
                    if (hr == 0)
                    {
                        return true;
                    }

                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: MoveWindowToDesktop failed, hwnd={FormatHwnd(hwnd)}, targetDesktopId={currentDesktopId}, attempt={attempt + 1}, hr={FormatHResult(hr)}");
                    Thread.Sleep(40);
                }

                return false;
            }
            catch (COMException ex)
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: MoveWindowToCurrentVirtualDesktop exception, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                return false;
            }
            catch
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: MoveWindowToCurrentVirtualDesktop exception, hwnd={FormatHwnd(hwnd)}");
                return false;
            }
        }

        private static bool TryGetCurrentVirtualDesktopId(
            IVirtualDesktopManager manager,
            out Guid desktopId)
        {
            desktopId = Guid.Empty;

            if (manager == null)
            {
                return false;
            }

            // Best-effort fast path: use foreground window desktop as "current desktop".
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(foreground, out var foregroundProcessId);
                if (foregroundProcessId != (uint)Environment.ProcessId
                    && TryGetDesktopIdForCurrentDesktopWindow(manager, foreground, out desktopId))
                {
                    return true;
                }
            }

            var resolved = false;
            var resolvedDesktopId = Guid.Empty;
            var sawDesktopQueryFailure = false;
            var firstDesktopQueryFailureHr = 0;
            var firstDesktopQueryFailureHwnd = IntPtr.Zero;

            try
            {
                _ = EnumWindows(
                    (hwnd, _) =>
                    {
                        if (!IsTopLevelWindow(hwnd))
                        {
                            return true;
                        }

                        if (IsWindowCloaked(hwnd))
                        {
                            return true;
                        }

                        var onCurrentDesktopHr = manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var isOnCurrentDesktop);
                        if (onCurrentDesktopHr != 0)
                        {
                            if (!sawDesktopQueryFailure)
                            {
                                sawDesktopQueryFailure = true;
                                firstDesktopQueryFailureHr = onCurrentDesktopHr;
                                firstDesktopQueryFailureHwnd = hwnd;
                            }

                            return true;
                        }

                        if (!isOnCurrentDesktop)
                        {
                            return true;
                        }

                        if (TryGetDesktopIdForCurrentDesktopWindow(manager, hwnd, out var candidateDesktopId))
                        {
                            resolvedDesktopId = candidateDesktopId;
                            resolved = true;
                            return false;
                        }

                        return true;
                    },
                    IntPtr.Zero);
            }
            catch
            {
                return false;
            }

            if (!resolved)
            {
                if (sawDesktopQueryFailure)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: unable to find probe window on current desktop due to query failures, firstHwnd={FormatHwnd(firstDesktopQueryFailureHwnd)}, firstHr={FormatHResult(firstDesktopQueryFailureHr)}");
                }

                return false;
            }

            desktopId = resolvedDesktopId;
            return desktopId != Guid.Empty;
        }

        private static bool TryGetDesktopIdForCurrentDesktopWindow(
            IVirtualDesktopManager manager,
            IntPtr hwnd,
            out Guid desktopId)
        {
            desktopId = Guid.Empty;

            if (manager == null || hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var onCurrentDesktopHr = manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var isOnCurrentDesktop);
                if (onCurrentDesktopHr != 0)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: IsWindowOnCurrentVirtualDesktop failed while resolving desktop id, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(onCurrentDesktopHr)}");
                    return false;
                }

                if (!isOnCurrentDesktop)
                {
                    return false;
                }

                var hr = manager.GetWindowDesktopId(hwnd, out desktopId);
                if (hr != 0)
                {
                    TopToolbar.Logging.AppLogger.LogWarning(
                        $"VirtualDesktop: GetWindowDesktopId failed while resolving current desktop, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(hr)}");
                }
                else if (desktopId == Guid.Empty)
                {
                    TopToolbar.Logging.AppLogger.LogInfo(
                        $"VirtualDesktop: GetWindowDesktopId returned empty id while resolving current desktop, hwnd={FormatHwnd(hwnd)}");
                }

                return hr == 0 && desktopId != Guid.Empty;
            }
            catch (COMException ex)
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: desktop id resolution exception, hwnd={FormatHwnd(hwnd)}, hr={FormatHResult(ex.HResult)}, message={ex.Message}");
                return false;
            }
            catch
            {
                TopToolbar.Logging.AppLogger.LogWarning(
                    $"VirtualDesktop: desktop id resolution exception, hwnd={FormatHwnd(hwnd)}");
                return false;
            }
        }

        private static string FormatHwnd(IntPtr hwnd)
        {
            return $"0x{unchecked((ulong)hwnd.ToInt64()):X}";
        }

        private static string FormatHResult(int hr)
        {
            return $"0x{unchecked((uint)hr):X8}";
        }

        public static bool IsWindowHandleValid(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && IsWindow(hwnd);
        }

        public static bool IsWindowCloaked(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
                return hr == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
