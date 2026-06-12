// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
        private readonly Border _rect;
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
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0x4E, 0xA3, 0xFF)),
                BorderThickness = new Thickness(2),
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

            Activate();
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
    }
}
