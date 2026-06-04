// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using WinUIEx;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private void EnsurePerMonitorV2()
        {
            try
            {
                // Attempt to set per-monitor V2 awareness at runtime (harmless if already set via manifest)
                SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 constant
            }
            catch
            {
            }
        }

        private void TryHookDpiMessages()
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                {
                    return;
                }

                _newWndProc = DpiWndProc; // keep delegate alive
                _oldWndProc = SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_newWndProc)); // GWL_WNDPROC = -4
            }
            catch
            {
            }
        }

        private IntPtr DpiWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_DPICHANGED = 0x02E0;
            const int WM_HOTKEY = 0x0312;
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            const int VK_ESCAPE = 0x1B;
            if (msg == WM_DPICHANGED)
            {
                // lParam points to a RECT in new DPI suggested size/pos
                try
                {
                    ResizeToContent();
                    if (!_isVisible)
                    {
                        PositionAtTopCenter();
                    }
                }
                catch
                {
                }
            }
            else if (msg == WM_HOTKEY)
            {
                if (wParam.ToInt32() == RadialHotKeyId)
                {
                    TryEnqueueRadialHotKeyPress();
                    return IntPtr.Zero;
                }
            }
            else if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && wParam.ToInt32() == VK_ESCAPE && _isRadialVisible)
            {
                HideRadialMenu();
                return IntPtr.Zero;
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        // P/Invoke to keep window topmost if WinUIEx helper not available
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private void MakeTopMost()
        {
            var handle = _hwnd != IntPtr.Zero ? _hwnd : this.GetWindowHandle();
            SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        private void ApplyFramelessStyles()
        {
            // Remove caption / border styles so only the toolbar content is visible
            const int GWL_STYLE = -16;
            const int GWL_EXSTYLE = -20;
            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_MINIMIZEBOX = 0x00020000;
            const int WS_MAXIMIZEBOX = 0x00010000;
            const int WS_SYSMENU = 0x00080000;
            const int WS_POPUP = unchecked((int)0x80000000);
            const int WS_VISIBLE = 0x10000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TOPMOST = 0x00000008;

            var h = _hwnd;
            int style = GetWindowLong(h, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
            style |= WS_POPUP | WS_VISIBLE;
            _ = SetWindowLong(h, GWL_STYLE, style);

            int exStyle = GetWindowLong(h, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            _ = SetWindowLong(h, GWL_EXSTYLE, exStyle);
        }

        private void ApplyTransparentBackground()
        {
            // Apply DesktopAcrylicBackdrop to enable proper Acrylic blur effect
            // Note: WS_EX_LAYERED conflicts with Acrylic, so we use system backdrop instead
            if (this.SystemBackdrop == null)
            {
                try
                {
                    // Use DesktopAcrylicBackdrop for true blur-behind effect
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                }
                catch
                {
                    // Fallback if DesktopAcrylic is not available
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

    }
}
