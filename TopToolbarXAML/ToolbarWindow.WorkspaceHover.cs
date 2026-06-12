// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Logging;
using TopToolbar.Services.Workspaces;
using TopToolbar.ViewModels;
using Windows.UI;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private readonly WorkspaceDefinitionStore _workspaceHoverDefinitionStore =
            new(null, new TopToolbar.Services.Providers.WorkspaceProviderConfigStore());
        private Flyout _workspaceHoverFlyout;
        private string _workspaceHoverWorkspaceId = string.Empty;
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
                _workspaceHoverFlyout?.IsOpen == true)
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
            _workspaceHoverFlyout?.Hide();
            HideWorkspaceItemOverlay();

            _workspaceHoverWorkspaceId = workspace.Id ?? string.Empty;
            var panel = new StackPanel
            {
                Spacing = 8,
                Width = 440,
                Padding = new Thickness(14),
            };

            var titleBlock = new TextBlock
            {
                Text = workspace.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 15,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var subtitleBlock = new TextBlock
            {
                Text = $"{workspace.Applications.Count} item(s)",
                FontSize = 11,
                Opacity = 0.62,
                Margin = new Thickness(0, 2, 0, 8),
            };
            panel.Children.Add(titleBlock);
            panel.Children.Add(subtitleBlock);

            var list = new StackPanel
            {
                Spacing = 6,
            };

            foreach (var app in workspace.Applications.Where(app => app != null).OrderBy(app => app.ZOrder))
            {
                list.Children.Add(CreateWorkspaceAppRow(workspace, app));
            }

            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 360,
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

            _workspaceHoverFlyout = new Flyout
            {
                Content = border,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
            };
            _workspaceHoverFlyout.Opened += (_, _) => _isContextMenuOpen = true;
            _workspaceHoverFlyout.Closed += (_, _) =>
            {
                _isContextMenuOpen = false;
                _workspaceHoverWorkspaceId = string.Empty;
                HideWorkspaceItemOverlay();
            };
            _workspaceHoverFlyout.ShowAt(target);
        }

        private FrameworkElement CreateWorkspaceAppRow(WorkspaceDefinition workspace, ApplicationDefinition app)
        {
            var deleteButton = CreateDeleteButton();

            var name = new TextBlock
            {
                Text = app.DisplayName,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var subtitle = new TextBlock
            {
                Text = BuildWorkspaceAppSubtitle(app),
                FontSize = 11,
                Opacity = 0.62,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 0,
            };
            textStack.Children.Add(name);
            textStack.Children.Add(subtitle);

            var iconHost = new Grid
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Background = TryGetBrush("SystemControlHighlightListLowBrush", Color.FromArgb(0x36, 0x80, 0x80, 0x80)),
            };
            iconHost.Children.Add(new FontIcon
            {
                Glyph = app.Minimized ? "\uE921" : "\uE8A7",
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.78,
            });

            var row = new Grid
            {
                MinHeight = 52,
                Padding = new Thickness(10, 8, 8, 8),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
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
                row.Background = TryGetBrush("SystemControlHighlightListLowBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                deleteButton.Opacity = 1;
                ShowWorkspaceItemOverlay(app);
            };
            row.PointerExited += (_, _) =>
            {
                row.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                deleteButton.Opacity = 0;
                HideWorkspaceItemOverlay();
            };
            deleteButton.Click += async (_, _) =>
            {
                await RemoveWorkspaceAppAsync(workspace.Id, app.Id).ConfigureAwait(true);
            };

            return row;
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
            if (!string.IsNullOrWhiteSpace(app.RemoteProvider))
            {
                return "Windows App connection";
            }

            if (!string.IsNullOrWhiteSpace(app.PackageFullName))
            {
                return app.PackageFullName;
            }

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                return app.Path;
            }

            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                return app.AppUserModelId;
            }

            return app.Minimized ? "Minimized window" : "Window";
        }

        private void ShowWorkspaceItemOverlay(ApplicationDefinition app)
        {
            var position = app?.Position;
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
                    _workspaceHoverFlyout?.Hide();
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
    }
}
