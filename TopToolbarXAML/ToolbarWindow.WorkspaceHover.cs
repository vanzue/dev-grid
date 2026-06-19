// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Logging;
using TopToolbar.Services.Workspaces;
using TopToolbar.Services.Windowing;
using TopToolbar.ViewModels;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private readonly WorkspaceDefinitionStore _workspaceHoverDefinitionStore =
            new(null, new TopToolbar.Services.Providers.WorkspaceProviderConfigStore());
        private WindowEx _workspaceHoverWindow;
        private Grid _workspaceHoverWindowRoot;
        private DispatcherQueueTimer _workspaceHoverDismissTimer;
        private readonly ManagedWindowRegistry _workspaceHoverWindows = WindowClaimer.Instance.Registry;
        private string _workspaceHoverWorkspaceId = string.Empty;
        private string _workspaceHoverAppId = string.Empty;
        private WorkspaceItemOverlayWindow _workspaceItemOverlay;

        private async void OnToolbarButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe ||
                fe.Tag is not ToolbarButtonItem item ||
                !TryGetRuntimeWorkspaceId(item.Button, out var workspaceId))
            {
                return;
            }

            if (string.Equals(_workspaceHoverWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase) &&
                _workspaceHoverWindow != null)
            {
                return;
            }

            try
            {
                var workspace = await _workspaceHoverDefinitionStore
                    .LoadByIdAsync(workspaceId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (workspace?.Applications == null || workspace.Applications.Count == 0)
                {
                    return;
                }

                await RunOnUiThreadAsync(() => ShowWorkspaceItemsFlyout(fe, workspace))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceHover: failed to load workspace '{workspaceId}' - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ShowWorkspaceItemsFlyout(FrameworkElement target, WorkspaceDefinition workspace)
        {
            CloseWorkspaceHoverWindow();
            HideWorkspaceItemOverlay();

            _workspaceHoverWorkspaceId = workspace.Id ?? string.Empty;
            var panel = new StackPanel
            {
                Spacing = 10,
                Width = 500,
                Padding = new Thickness(22, 20, 22, 22),
            };

            var titleBlock = new TextBlock
            {
                Text = workspace.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 17,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var subtitleBlock = new TextBlock
            {
                Text = $"{workspace.Applications.Count} item(s)",
                FontSize = 11,
                Opacity = 0.58,
                Margin = new Thickness(0, 2, 0, 10),
            };
            panel.Children.Add(titleBlock);
            panel.Children.Add(subtitleBlock);

            var list = new StackPanel
            {
                Spacing = 6,
            };

            var isHotWorkspace = WorkspaceKinds.IsHot(workspace);
            foreach (var app in workspace.Applications
                .Where(app => app != null)
                .OrderBy(app => app.Minimized)
                .ThenBy(app => app.ZOrder)
                .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                list.Children.Add(CreateWorkspaceAppRow(workspace, app, isHotWorkspace));
            }

            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            panel.Children.Add(scroller);

            var border = new Border
            {
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(18),
                Background = TryGetBrush("SystemControlAcrylicWindowBrush", Color.FromArgb(0xF8, 0xF5, 0xF1, 0xEA)),
                BorderBrush = TryGetBrush("SystemControlForegroundBaseLowBrush", Color.FromArgb(0x55, 0x60, 0x66, 0x66)),
                BorderThickness = new Thickness(1),
                Child = panel,
            };

            _workspaceHoverWindow = new WindowEx
            {
                Title = string.Empty,
                IsTitleBarVisible = false,
                ExtendsContentIntoTitleBar = true,
            };
            _workspaceHoverWindow.SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));
            _workspaceHoverWindowRoot = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            };
            _workspaceHoverWindowRoot.Children.Add(border);
            _workspaceHoverWindowRoot.PointerEntered += (_, _) => StopWorkspaceHoverDismissTimer();
            _workspaceHoverWindowRoot.PointerExited += (_, _) => StartWorkspaceHoverDismissTimer();
            _workspaceHoverWindow.Content = _workspaceHoverWindowRoot;
            _workspaceHoverWindow.Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                {
                    StartWorkspaceHoverDismissTimer();
                }
                else
                {
                    StopWorkspaceHoverDismissTimer();
                }
            };
            _workspaceHoverWindow.Closed += (_, _) =>
            {
                _isContextMenuOpen = false;
                _workspaceHoverWorkspaceId = string.Empty;
                _workspaceHoverAppId = string.Empty;
                HideWorkspaceItemOverlay();
                _workspaceHoverWindow = null;
                _workspaceHoverWindowRoot = null;
            };

            _workspaceHoverWindow.Activate();
            ConfigureWorkspaceHoverWindow(target);
            _isContextMenuOpen = true;
        }

        private void ConfigureWorkspaceHoverWindow(FrameworkElement target)
        {
            var appWindow = _workspaceHoverWindow?.AppWindow;
            if (appWindow == null)
            {
                return;
            }

            appWindow.IsShownInSwitchers = false;
            appWindow.SetIcon(null);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            const int width = 532;
            const int height = 500;
            appWindow.Resize(new SizeInt32(width, height));
            ApplyWorkspaceHoverWindowStyles();

            var toolbarPos = AppWindow?.Position ?? new PointInt32(0, 0);
            var toolbarSize = AppWindow?.Size ?? new SizeInt32(width, 120);
            var x = toolbarPos.X + 24;
            var y = toolbarPos.Y + Math.Max(toolbarSize.Height - 36, 0);
            try
            {
                if (target?.XamlRoot != null)
                {
                    var transform = target.TransformToVisual(RootGrid);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    var scale = target.XamlRoot.RasterizationScale <= 0 ? 1d : target.XamlRoot.RasterizationScale;
                    x = toolbarPos.X + (int)Math.Round(point.X * scale) - 24;
                }
            }
            catch
            {
            }

            appWindow.Move(new PointInt32(x, y));
        }

        private void ApplyWorkspaceHoverWindowStyles()
        {
            var hwnd = _workspaceHoverWindow?.GetWindowHandle() ?? IntPtr.Zero;
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
                exStyle |= wsExToolWindow | wsExTopmost;
                _ = SetWindowLong(hwnd, gwlExStyle, exStyle);

                _ = SetWindowPos(
                    hwnd,
                    new IntPtr(-1),
                    0,
                    0,
                    0,
                    0,
                    (uint)(swpNoMove | swpNoSize | swpNoActivate | swpShowWindow | swpFrameChanged));

                const int dwmwaBorderColor = 34;
                uint dwmColorNone = 0xFFFFFFFE;
                _ = DwmSetWindowAttribute(hwnd, dwmwaBorderColor, ref dwmColorNone, sizeof(uint));
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceHover: failed to apply popup window styles - {ex.Message}");
            }
        }

        private void CloseWorkspaceHoverWindow()
        {
            StopWorkspaceHoverDismissTimer();
            try
            {
                _workspaceHoverWindow?.Close();
            }
            catch
            {
            }

            _workspaceHoverWindow = null;
            _workspaceHoverWindowRoot = null;
            _workspaceHoverWorkspaceId = string.Empty;
            _workspaceHoverAppId = string.Empty;
        }

        private void StartWorkspaceHoverDismissTimer()
        {
            if (DispatcherQueue == null)
            {
                CloseWorkspaceHoverWindow();
                return;
            }

            _workspaceHoverDismissTimer ??= DispatcherQueue.CreateTimer();
            _workspaceHoverDismissTimer.Stop();
            _workspaceHoverDismissTimer.Interval = TimeSpan.FromMilliseconds(260);
            _workspaceHoverDismissTimer.IsRepeating = false;
            _workspaceHoverDismissTimer.Tick -= OnWorkspaceHoverDismissTimerTick;
            _workspaceHoverDismissTimer.Tick += OnWorkspaceHoverDismissTimerTick;
            _workspaceHoverDismissTimer.Start();
        }

        private void StopWorkspaceHoverDismissTimer()
        {
            try
            {
                _workspaceHoverDismissTimer?.Stop();
            }
            catch
            {
            }
        }

        private void OnWorkspaceHoverDismissTimerTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (IsCursorInsideWorkspaceHoverWindow())
            {
                return;
            }

            CloseWorkspaceHoverWindow();
        }

        private bool IsCursorInsideWorkspaceHoverWindow()
        {
            try
            {
                var appWindow = _workspaceHoverWindow?.AppWindow;
                if (appWindow == null)
                {
                    return false;
                }

                GetCursorPos(out var point);
                var position = appWindow.Position;
                var size = appWindow.Size;
                return point.X >= position.X &&
                    point.X <= position.X + size.Width &&
                    point.Y >= position.Y &&
                    point.Y <= position.Y + size.Height;
            }
            catch
            {
                return false;
            }
        }

        private FrameworkElement CreateWorkspaceAppRow(WorkspaceDefinition workspace, ApplicationDefinition app, bool isHotWorkspace)
        {
            var deleteButton = CreateDeleteButton();
            var boundWindow = GetBoundWorkspaceWindow(app);
            var isMissing = isHotWorkspace && !app.Minimized && boundWindow == IntPtr.Zero;

            var name = new TextBlock
            {
                Text = app.DisplayName,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 360,
            };
            var subtitle = new TextBlock
            {
                Text = BuildWorkspaceAppSubtitle(app),
                FontSize = 11,
                Opacity = 0.58,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
                MaxWidth = 360,
            };
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 0,
                Margin = new Thickness(12, 0, 8, 0),
            };
            textStack.Children.Add(name);
            textStack.Children.Add(subtitle);

            var iconHost = new Grid
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(isMissing
                    ? Color.FromArgb(0x28, 0xD1, 0x34, 0x38)
                    : Color.FromArgb(0x36, 0x80, 0x80, 0x80)),
                CornerRadius = new CornerRadius(8),
            };
            iconHost.Children.Add(new FontIcon
            {
                Glyph = isMissing ? "\uE711" : app.Minimized ? "\uE921" : "\uE8A7",
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.78,
            });
            ToolTipService.SetToolTip(iconHost, isMissing ? "Window missing" : app.Minimized ? "Minimized" : "Restored");

            var row = new Grid
            {
                MinHeight = 52,
                Padding = new Thickness(12, 9, 8, 9),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(isMissing ? Color.FromArgb(0x20, 0xD1, 0x34, 0x38) : Color.FromArgb(0, 0, 0, 0)),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(iconHost, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(deleteButton, 2);
            row.Children.Add(iconHost);
            row.Children.Add(textStack);
            row.Children.Add(deleteButton);

            row.PointerEntered += (_, _) =>
            {
                if (string.Equals(_workspaceHoverAppId, app.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _workspaceHoverAppId = app.Id ?? string.Empty;
                row.Background = new SolidColorBrush(isMissing
                    ? Color.FromArgb(0x34, 0xD1, 0x34, 0x38)
                    : Color.FromArgb(0x42, 0x7E, 0xA9, 0xE8));
                iconHost.Background = new SolidColorBrush(isMissing
                    ? Color.FromArgb(0x36, 0xD1, 0x34, 0x38)
                    : Color.FromArgb(0x44, 0x2D, 0x7D, 0xFF));
                deleteButton.Opacity = 1;
                if (!isMissing)
                {
                    ShowWorkspaceItemOverlay(app);
                }
            };
            row.PointerExited += (_, _) =>
            {
                row.Background = new SolidColorBrush(isMissing ? Color.FromArgb(0x20, 0xD1, 0x34, 0x38) : Color.FromArgb(0, 0, 0, 0));
                iconHost.Background = new SolidColorBrush(isMissing
                    ? Color.FromArgb(0x28, 0xD1, 0x34, 0x38)
                    : Color.FromArgb(0x36, 0x80, 0x80, 0x80));
                deleteButton.Opacity = 0;
                if (string.Equals(_workspaceHoverAppId, app.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _workspaceHoverAppId = string.Empty;
                }

                HideWorkspaceItemOverlay();
            };
            deleteButton.Click += async (_, _) =>
            {
                await RemoveWorkspaceAppAsync(workspace.Id, app.Id).ConfigureAwait(true);
            };
            row.RightTapped += (_, e) =>
            {
                e.Handled = true;
                ShowWorkspaceAppContextMenu(row, workspace, app, e.GetPosition(row));
            };
            row.Tapped += async (_, _) =>
            {
                if (!isMissing && isHotWorkspace)
                {
                    await RestoreWorkspaceAppWindowAsync(app).ConfigureAwait(true);
                }
            };

            return row;
        }

        private IntPtr GetBoundWorkspaceWindow(ApplicationDefinition app)
        {
            var hwnd = _workspaceHoverWindows.GetBoundWindow(app?.Id);
            if (hwnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return NativeWindowHelper.TryCreateWindowInfo(hwnd, out _) ? hwnd : IntPtr.Zero;
        }

        private async System.Threading.Tasks.Task RestoreWorkspaceAppWindowAsync(ApplicationDefinition app)
        {
            var hwnd = GetBoundWorkspaceWindow(app);
            if (hwnd == IntPtr.Zero)
            {
                _notificationService.ShowWarning("Window is no longer available.");
                return;
            }

            var position = app?.Position;
            if (position == null || position.IsEmpty)
            {
                _ = NativeWindowHelper.TryActivateWindow(hwnd);
                return;
            }

            await NativeWindowHelper.SetWindowPlacementAsync(
                    hwnd,
                    new WindowPlacement(position.X, position.Y, position.Width, position.Height),
                    maximize: app.Maximized,
                    minimize: false,
                    waitForInputIdle: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(true);
            _ = NativeWindowHelper.TryActivateWindow(hwnd);
        }

        private void ShowWorkspaceAppContextMenu(
            FrameworkElement target,
            WorkspaceDefinition workspace,
            ApplicationDefinition app,
            Windows.Foundation.Point position)
        {
            if (target == null || workspace == null || app == null)
            {
                return;
            }

            var menu = new MenuFlyout();
            var removeItem = new MenuFlyoutItem
            {
                Text = "Remove",
            };
            removeItem.Click += async (_, _) =>
            {
                await RemoveWorkspaceAppAsync(workspace.Id, app.Id).ConfigureAwait(true);
            };
            menu.Items.Add(removeItem);
            WireContextMenuAutoHide(menu);
            menu.ShowAt(target, position);
        }

        private Button CreateDeleteButton()
        {
            var button = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Opacity = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(button, "Remove from workspace");
            button.Content = new Grid
            {
                Children =
                {
                    new Ellipse
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xD1, 0x34, 0x38)),
                    },
                    new FontIcon
                    {
                        Glyph = "\uE711",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x20, 0x28)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            return button;
        }

        private static string BuildWorkspaceAppSubtitle(ApplicationDefinition app)
        {
            var statePrefix = app.Minimized ? "Minimized · " : string.Empty;
            if (!string.IsNullOrWhiteSpace(app.RemoteProvider))
            {
                return statePrefix + "Windows App connection";
            }

            if (!string.IsNullOrWhiteSpace(app.PackageFullName))
            {
                return statePrefix + app.PackageFullName;
            }

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                return statePrefix + app.Path;
            }

            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                return statePrefix + app.AppUserModelId;
            }

            return app.Minimized ? "Minimized window" : "Window";
        }

        private void ShowWorkspaceItemOverlay(ApplicationDefinition app)
        {
            if (app?.Minimized == true)
            {
                HideWorkspaceItemOverlay();
                return;
            }

            var position = app?.Position;
            if (string.IsNullOrWhiteSpace(_workspaceHoverAppId))
            {
                _workspaceHoverAppId = app?.Id ?? string.Empty;
            }

            if (position == null || position.IsEmpty)
            {
                HideWorkspaceItemOverlay();
                return;
            }

            _workspaceItemOverlay ??= new WorkspaceItemOverlayWindow();
            _workspaceItemOverlay.Show(new Services.Windowing.WindowBounds(
                position.X,
                position.Y,
                position.X + position.Width,
                position.Y + position.Height));
        }

        private void HideWorkspaceItemOverlay()
        {
            _workspaceItemOverlay?.Hide();
        }

        private async System.Threading.Tasks.Task RemoveWorkspaceAppAsync(string workspaceId, string appId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(appId))
            {
                return;
            }

            try
            {
                var workspace = await _workspaceHoverDefinitionStore
                    .LoadByIdAsync(workspaceId, CancellationToken.None)
                    .ConfigureAwait(true);
                if (workspace?.Applications == null)
                {
                    return;
                }

                var removed = workspace.Applications.RemoveAll(app =>
                    app != null && string.Equals(app.Id, appId, StringComparison.OrdinalIgnoreCase));
                if (removed <= 0)
                {
                    return;
                }

                await _workspaceHoverDefinitionStore.SaveWorkspaceAsync(workspace, CancellationToken.None)
                    .ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    CloseWorkspaceHoverWindow();
                    HideWorkspaceItemOverlay();
                    _notificationService.ShowSuccess("Removed workspace item.");
                }).ConfigureAwait(false);
                await RefreshWorkspaceGroupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceHover: failed to remove app '{appId}' from workspace '{workspaceId}' - {ex.Message}");
                _notificationService.ShowError("Failed to remove workspace item: " + ex.Message);
            }
        }

        private Brush TryGetBrush(string key, Color fallback)
        {
            try
            {
                if (RootGrid?.Resources != null &&
                    RootGrid.Resources.TryGetValue(key, out var value) &&
                    value is Brush brush)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);
    }
}
