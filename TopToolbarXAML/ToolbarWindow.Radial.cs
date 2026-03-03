// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Controls;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.ViewModels;
using Windows.System;
using Windows.UI;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private const int RadialHotKeyId = 0x5452;
        private const uint ModAlt = 0x0001;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkSpace = 0x20;

        private static readonly SolidColorBrush RadialBackgroundBrush = new(Color.FromArgb(230, 28, 28, 32));
        private static readonly SolidColorBrush RadialBorderBrush = new(Color.FromArgb(130, 255, 255, 255));
        private static readonly SolidColorBrush RadialRingBrush = new(Color.FromArgb(60, 255, 255, 255));
        private static readonly SolidColorBrush RadialCenterBrush = new(Color.FromArgb(235, 20, 20, 22));
        private static readonly SolidColorBrush RadialCenterTextBrush = new(Color.FromArgb(230, 230, 230, 230));
        private static readonly SolidColorBrush RadialIconBrush = new(Color.FromArgb(255, 255, 255, 255));

        private ToolbarDisplayMode _currentDisplayMode = ToolbarDisplayMode.TopBar;
        private bool _radialHotKeyRegistered;
        private bool _isRadialVisible;
        private bool _isShowingRadial;
        private System.Timers.Timer _radialHotKeyPollTimer;
        private bool _radialFallbackPolling;
        private bool _lastAltSpaceDown;
        private long _lastRadialHotKeyTriggerTick;

        private enum RadialEntryKind
        {
            ToolbarButton,
            Snapshot,
            Settings,
        }

        private sealed class RadialEntry
        {
            public RadialEntryKind Kind { get; init; }

            public string Label { get; init; } = string.Empty;

            public ToolbarButtonItem Item { get; init; }

            public ToolbarButton IconButton { get; init; }
        }

        private void ApplyDisplayMode(ToolbarDisplayMode mode)
        {
            _currentDisplayMode = mode;

            if (_currentDisplayMode == ToolbarDisplayMode.RadialMenu)
            {
                StopMonitoring();
                HideToolbar();
                ToolbarContainer.Visibility = Visibility.Collapsed;
                EnsureRadialHotKey();
                StartRadialHotKeyFallbackPolling();
                return;
            }

            HideRadialMenu();
            UnregisterRadialHotKey();
            ToolbarContainer.Visibility = Visibility.Visible;
            StartMonitoring();
        }

        private void EnsureRadialHotKey()
        {
            if (_radialHotKeyRegistered || _hwnd == IntPtr.Zero)
            {
                return;
            }

            var ok = RegisterHotKey(_hwnd, RadialHotKeyId, ModAlt | ModNoRepeat, VkSpace);
            if (!ok)
            {
                AppLogger.LogWarning("RadialMenu: failed to register Alt+Space hotkey.");
                return;
            }

            _radialHotKeyRegistered = true;
            AppLogger.LogInfo("RadialMenu: Alt+Space hotkey registered.");
        }

        private void UnregisterRadialHotKey()
        {
            if (_radialHotKeyRegistered && _hwnd != IntPtr.Zero)
            {
                _ = UnregisterHotKey(_hwnd, RadialHotKeyId);
            }

            _radialHotKeyRegistered = false;
            StopRadialHotKeyFallbackPolling(disposeTimer: false);
        }

        private void OnRadialHotKeyPressed()
        {
            if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
            {
                _ = DispatcherQueue.TryEnqueue(OnRadialHotKeyPressed);
                return;
            }

            if (_currentDisplayMode != ToolbarDisplayMode.RadialMenu)
            {
                return;
            }

            if (_isRadialVisible)
            {
                // Keep radial visible; close is explicitly Esc or action click.
                return;
            }

            _ = ShowRadialMenuAtCursorAsync();
        }

        private void StartRadialHotKeyFallbackPolling()
        {
            if (_radialFallbackPolling)
            {
                return;
            }

            _radialHotKeyPollTimer ??= new System.Timers.Timer(35)
            {
                AutoReset = true,
            };

            _radialHotKeyPollTimer.Elapsed -= OnRadialHotKeyPollElapsed;
            _radialHotKeyPollTimer.Elapsed += OnRadialHotKeyPollElapsed;
            _lastAltSpaceDown = false;
            _radialFallbackPolling = true;
            _radialHotKeyPollTimer.Start();
            AppLogger.LogInfo("RadialMenu: Alt+Space polling enabled.");
        }

        private void StopRadialHotKeyFallbackPolling(bool disposeTimer)
        {
            _radialFallbackPolling = false;
            _lastAltSpaceDown = false;

            if (_radialHotKeyPollTimer == null)
            {
                return;
            }

            _radialHotKeyPollTimer.Stop();
            _radialHotKeyPollTimer.Elapsed -= OnRadialHotKeyPollElapsed;

            if (disposeTimer)
            {
                _radialHotKeyPollTimer.Dispose();
                _radialHotKeyPollTimer = null;
            }
        }

        private void OnRadialHotKeyPollElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_currentDisplayMode != ToolbarDisplayMode.RadialMenu)
            {
                return;
            }

            const int VkMenu = 0x12;
            const int VkSpaceInt = 0x20;
            var altDown = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
            var spaceDown = (GetAsyncKeyState(VkSpaceInt) & 0x8000) != 0;
            var comboDown = altDown && spaceDown;

            if (comboDown && !_lastAltSpaceDown)
            {
                TryEnqueueRadialHotKeyPress();
            }

            _lastAltSpaceDown = comboDown;
        }

        private void TryEnqueueRadialHotKeyPress()
        {
            var now = Environment.TickCount64;
            var elapsed = now - _lastRadialHotKeyTriggerTick;
            if (elapsed >= 0 && elapsed < 180)
            {
                return;
            }

            _lastRadialHotKeyTriggerTick = now;
            _ = DispatcherQueue?.TryEnqueue(OnRadialHotKeyPressed);
        }

        private async Task ShowRadialMenuAtCursorAsync()
        {
            if (_isShowingRadial || _isRadialVisible)
            {
                return;
            }

            _isShowingRadial = true;

            try
            {
                await RefreshWorkspaceGroupAsync().ConfigureAwait(true);
                await EnqueueOnUiThreadAsync(() =>
                {
                    var entries = BuildRadialEntries();
                    if (entries.Count == 0)
                    {
                        return;
                    }

                    var workspaceEntryCount = 0;
                    foreach (var entry in entries)
                    {
                        if (entry.Kind == RadialEntryKind.ToolbarButton &&
                            entry.Label.StartsWith("Workspace:", StringComparison.OrdinalIgnoreCase))
                        {
                            workspaceEntryCount++;
                        }
                    }

                    AppLogger.LogInfo($"RadialMenu: entries={entries.Count}, workspaceEntries={workspaceEntryCount}.");

                    BuildRadialVisualTree(entries, out var diameterDip);

                    var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
                    var sizePx = (int)Math.Ceiling(diameterDip * scale);
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(sizePx, sizePx));

                    GetCursorPos(out var cursor);
                    var targetX = cursor.X - (sizePx / 2);
                    var targetY = cursor.Y - (sizePx / 2);

                    var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
                    if (displayArea != null)
                    {
                        var workArea = displayArea.WorkArea;
                        targetX = Math.Clamp(targetX, workArea.X, workArea.X + workArea.Width - sizePx);
                        targetY = Math.Clamp(targetY, workArea.Y, workArea.Y + workArea.Height - sizePx);
                    }

                    AppWindow.Move(new Windows.Graphics.PointInt32(targetX, targetY));
                    AppWindow.Show(true);
                    Activate();
                    MakeTopMost();

                    AppLogger.LogInfo($"RadialMenu: show cursor=({cursor.X},{cursor.Y}) target=({targetX},{targetY}) size={sizePx}.");

                    _isRadialVisible = true;
                    _isVisible = false;

                    RadialCanvas.Visibility = Visibility.Visible;
                    ToolbarContainer.Visibility = Visibility.Collapsed;
                    RootGrid.Focus(FocusState.Programmatic);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("RadialMenu: failed while showing radial menu.", ex);
            }
            finally
            {
                _isShowingRadial = false;
            }
        }

        private Task EnqueueOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("RadialMenu: failed to enqueue UI work."));
            }

            return tcs.Task;
        }

        private void HideRadialMenu()
        {
            if (!_isRadialVisible)
            {
                return;
            }

            _isRadialVisible = false;
            RadialCanvas.Visibility = Visibility.Collapsed;
            AppWindow.Hide();
        }

        private List<RadialEntry> BuildRadialEntries()
        {
            var entries = new List<RadialEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in ItemsViewModel.VisibleGroups)
            {
                if (!IsWorkspaceGroup(group))
                {
                    continue;
                }

                AppendGroupButtons(entries, seen, group, "Workspace");
            }

            foreach (var group in ItemsViewModel.VisibleGroups)
            {
                if (IsWorkspaceGroup(group))
                {
                    continue;
                }

                var labelPrefix = string.IsNullOrWhiteSpace(group?.Group?.Name) ? "App" : group.Group.Name;
                AppendGroupButtons(entries, seen, group, labelPrefix);
            }

            entries.Add(new RadialEntry
            {
                Kind = RadialEntryKind.Snapshot,
                Label = "Snapshot",
                IconButton = new ToolbarButton
                {
                    Name = "Snapshot",
                    IconType = ToolbarIconType.Catalog,
                    IconGlyph = "\uE722",
                },
            });

            entries.Add(new RadialEntry
            {
                Kind = RadialEntryKind.Settings,
                Label = "Settings",
                IconButton = new ToolbarButton
                {
                    Name = "Settings",
                    IconType = ToolbarIconType.Catalog,
                    IconGlyph = "\uE713",
                },
            });

            return entries;
        }

        private static bool IsWorkspaceGroup(ToolbarGroupViewModel group)
        {
            if (group == null)
            {
                return false;
            }

            if (string.Equals(group.GroupId, "workspaces", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(group.GroupId, "WorkspaceProvider", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var providers = group.Group?.Providers;
            if (providers == null)
            {
                return false;
            }

            foreach (var providerId in providers)
            {
                if (string.Equals(providerId, "WorkspaceProvider", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AppendGroupButtons(
            List<RadialEntry> entries,
            HashSet<string> seen,
            ToolbarGroupViewModel group,
            string labelPrefix)
        {
            if (entries == null || seen == null || group == null)
            {
                return;
            }

            foreach (var button in group.Buttons)
            {
                if (button?.Button == null || !button.IsEnabled)
                {
                    continue;
                }

                var key = $"{group.GroupId}|{button.Button.Id}";
                if (!seen.Add(key))
                {
                    continue;
                }

                entries.Add(new RadialEntry
                {
                    Kind = RadialEntryKind.ToolbarButton,
                    Label = $"{labelPrefix}: {button.Button.DisplayName}",
                    Item = button,
                    IconButton = button.Button,
                });
            }
        }

        private void BuildRadialVisualTree(IReadOnlyList<RadialEntry> entries, out double diameterDip)
        {
            const double itemSize = 62;
            const double minRingRadius = 114;
            const double ringSpacing = 78;
            const double ringPadding = 56;

            var rings = CreateRings(entries.Count, minRingRadius, itemSize, ringSpacing);
            var outerRadius = rings.Count == 0 ? minRingRadius : rings[rings.Count - 1].radius;
            var center = outerRadius + (itemSize / 2d) + 22;
            diameterDip = Math.Ceiling((center * 2d) + ringPadding);

            RadialCanvas.Width = diameterDip;
            RadialCanvas.Height = diameterDip;
            RadialCanvas.Children.Clear();

            var outerRingDiameter = (outerRadius * 2d) + itemSize;
            var ring = new Ellipse
            {
                Width = outerRingDiameter,
                Height = outerRingDiameter,
                Fill = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Stroke = RadialRingBrush,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(ring, center - (outerRingDiameter / 2d));
            Canvas.SetTop(ring, center - (outerRingDiameter / 2d));
            RadialCanvas.Children.Add(ring);

            var centerNode = BuildCenterNode();
            Canvas.SetLeft(centerNode, center - (centerNode.Width / 2d));
            Canvas.SetTop(centerNode, center - (centerNode.Height / 2d));
            RadialCanvas.Children.Add(centerNode);

            var index = 0;
            foreach (var (radius, count) in rings)
            {
                for (var i = 0; i < count && index < entries.Count; i++, index++)
                {
                    var angle = (-Math.PI / 2d) + ((2d * Math.PI * i) / count);
                    var x = center + (Math.Cos(angle) * radius) - (itemSize / 2d);
                    var y = center + (Math.Sin(angle) * radius) - (itemSize / 2d);
                    var button = BuildRadialButton(entries[index], itemSize);
                    Canvas.SetLeft(button, x);
                    Canvas.SetTop(button, y);
                    RadialCanvas.Children.Add(button);
                }
            }
        }

        private static List<(double radius, int count)> CreateRings(int totalCount, double minRadius, double itemSize, double ringSpacing)
        {
            var rings = new List<(double radius, int count)>();
            if (totalCount <= 0)
            {
                return rings;
            }

            var remaining = totalCount;
            var radius = minRadius;
            while (remaining > 0)
            {
                var circumference = 2d * Math.PI * radius;
                var capacity = Math.Max(6, (int)Math.Floor(circumference / (itemSize + 12)));
                var count = Math.Min(remaining, capacity);
                rings.Add((radius, count));
                remaining -= count;
                radius += ringSpacing;
            }

            return rings;
        }

        private FrameworkElement BuildCenterNode()
        {
            var border = new Border
            {
                Width = 98,
                Height = 98,
                CornerRadius = new CornerRadius(49),
                Background = RadialCenterBrush,
                BorderBrush = RadialBorderBrush,
                BorderThickness = new Thickness(1),
            };

            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = "\uE70F",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = RadialIconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Esc",
                FontSize = 12,
                Foreground = RadialCenterTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            border.Child = stack;
            return border;
        }

        private Button BuildRadialButton(RadialEntry entry, double itemSize)
        {
            var button = new Button
            {
                Width = itemSize,
                Height = itemSize,
                CornerRadius = new CornerRadius(itemSize / 2d),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                Background = RadialBackgroundBrush,
                BorderBrush = RadialBorderBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = entry,
            };

            ToolTipService.SetToolTip(button, entry.Label);

            var icon = new ToolbarIconPresenter
            {
                Button = entry.IconButton,
                IconSize = 28,
                Foreground = Color.FromArgb(255, 255, 255, 255),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Content = icon;
            button.Click += OnRadialButtonClick;
            return button;
        }

        private async void OnRadialButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not RadialEntry entry)
            {
                return;
            }

            HideRadialMenu();

            try
            {
                switch (entry.Kind)
                {
                    case RadialEntryKind.ToolbarButton:
                        if (entry.Item != null)
                        {
                            await _actionExecutor.ExecuteAsync(entry.Item.Group, entry.Item.Button, CancellationToken.None).ConfigureAwait(false);
                        }
                        break;
                    case RadialEntryKind.Snapshot:
                        await HandleSnapshotButtonClickAsync(null).ConfigureAwait(true);
                        break;
                    case RadialEntryKind.Settings:
                        OpenSettingsWindow();
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("RadialMenu: action execution failed.", ex);
            }
        }

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && _isRadialVisible)
            {
                HideRadialMenu();
                e.Handled = true;
            }
        }
    }
}
