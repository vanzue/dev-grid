// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TopToolbar.Services.Windowing
{
    internal static partial class NativeWindowHelper
    {
        private const int SwShowNormal = 1;
        private const int SwShowMinimized = 2;
        private const int SwShowMaximized = 3;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint GwOwner = 4;
        private const int GwlpStyle = -16;
        private const int GwlpExStyle = -20;
        private const long WsDisabled = 0x08000000L;
        private const long WsVisible = 0x10000000L;
        private const long WsMinimize = 0x20000000L;
        private const long WsChild = 0x40000000L;
        private const long WsMinimizeBox = 0x00020000L;
        private const long WsExToolWindow = 0x00000080L;
        private const int DwmwaCloaked = 14;
        private const ushort ValueTypeLpwstr = 31;
        private const int WaitForInputIdleTimeoutMilliseconds = 5000;
        private const int WindowVisibilityTimeoutMilliseconds = 5000;
        private const int WindowVisibilityPollMilliseconds = 50;
        private const int WindowPlacementRetryDelayMilliseconds = 150;
        private const int WindowPlacementRetryAttempts = 30;
        private const int WindowPlacementTolerancePixels = 8;
        private static readonly IntPtr HwndTopMost = new(-1);
        private static readonly IntPtr HwndNoTopMost = new(-2);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags
        );

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr hWnd,
            StringBuilder lpClassName,
            int nMaxCount
        );

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowPlacement(
            IntPtr hWnd,
            ref NativeWindowPlacement lpwndpl
        );

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(
            IntPtr hProcess,
            ref int packageFullNameLength,
            StringBuilder packageFullName
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            ProcessAccess desiredAccess,
            bool inheritHandle,
            uint processId
        );

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            int dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            out IntPtr propertyStore
        );

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            out int pvAttribute,
            int cbAttribute
        );

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPlacement
        {
            public int Length;

            public int Flags;

            public int ShowCmd;

            public NativePoint MinPosition;

            public NativePoint MaxPosition;

            public NativeRect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;

            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [Flags]
        private enum ProcessAccess : uint
        {
            QueryLimitedInformation = 0x1000,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort ValueType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr PointerValue;
            public int IntValue;
            public int Reserved4;
            public int Reserved5;
        }

        [ComImport]
        [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop(
                IntPtr topLevelWindow,
                [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop
            );

            [PreserveSig]
            int GetWindowDesktopId(
                IntPtr topLevelWindow,
                out Guid desktopId
            );

            [PreserveSig]
            int MoveWindowToDesktop(
                IntPtr topLevelWindow,
                ref Guid desktopId
            );
        }

        [ComImport]
        [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
        private class VirtualDesktopManager
        {
        }
    }
}
