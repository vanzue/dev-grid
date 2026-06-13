// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Services.Windowing;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    internal sealed class WorkspaceItemOverlayWindow : WindowEx, IDisposable
    {
        private static readonly IntPtr HwndTopMost = new(-1);
        private readonly Border _rect;
        private WindowBounds _lastBounds;
        private bool _stylesApplied;
        private bool _closed;

        public WorkspaceItemOverlayWindow()
        {
            Title = string.Empty;
            IsTitleBarVisible = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            _rect = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x42, 0x2D, 0x7D, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Content = _rect;
        }

        public void Show(WindowBounds bounds)
        {
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                Hide();
                return;
            }

            if (_lastBounds.Left == bounds.Left &&
                _lastBounds.Top == bounds.Top &&
                _lastBounds.Right == bounds.Right &&
                _lastBounds.Bottom == bounds.Bottom &&
                AppWindow?.IsVisible == true)
            {
                return;
            }

            _lastBounds = bounds;
            Activate();
            ApplyFramelessStyles();
            if (AppWindow != null)
            {
                AppWindow.IsShownInSwitchers = false;
                AppWindow.SetIcon(null);
                AppWindow.Move(new PointInt32(bounds.Left, bounds.Top));
                AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }
            }
        }

        private void ApplyFramelessStyles()
        {
            if (_stylesApplied)
            {
                return;
            }

            var hwnd = this.GetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                const int gwlStyle = -16;
                const int gwlExStyle = -20;
                const int wsCaption = 0x00C00000;
                const int wsThickFrame = 0x00040000;
                const int wsMinimizeBox = 0x00020000;
                const int wsMaximizeBox = 0x00010000;
                const int wsSysMenu = 0x00080000;
                const int wsPopup = unchecked((int)0x80000000);
                const int wsVisible = 0x10000000;
                const int wsExToolWindow = 0x00000080;
                const int wsExTopmost = 0x00000008;
                const int wsExNoActivate = 0x08000000;
                const int wsExTransparent = 0x00000020;
                const int wsExLayered = 0x00080000;
                const int swpNoMove = 0x0002;
                const int swpNoSize = 0x0001;
                const int swpNoActivate = 0x0010;
                const int swpShowWindow = 0x0040;
                const int swpFrameChanged = 0x0020;

                var style = GetWindowLong(hwnd, gwlStyle);
                style &= ~(wsCaption | wsThickFrame | wsMinimizeBox | wsMaximizeBox | wsSysMenu);
                style |= wsPopup | wsVisible;
                _ = SetWindowLong(hwnd, gwlStyle, style);

                var exStyle = GetWindowLong(hwnd, gwlExStyle);
                exStyle |= wsExToolWindow | wsExTopmost | wsExNoActivate | wsExTransparent | wsExLayered;
                _ = SetWindowLong(hwnd, gwlExStyle, exStyle);

                _ = SetWindowPos(
                    hwnd,
                    HwndTopMost,
                    0,
                    0,
                    0,
                    0,
                    swpNoMove | swpNoSize | swpNoActivate | swpShowWindow | swpFrameChanged);

                const int dwmwaBorderColor = 34;
                uint dwmColorNone = 0xFFFFFFFE;
                _ = DwmSetWindowAttribute(hwnd, dwmwaBorderColor, ref dwmColorNone, sizeof(uint));
                _stylesApplied = true;
            }
            catch
            {
            }
        }

        public void Hide()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                AppWindow?.Hide();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                Close();
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            int uFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);
    }
}
