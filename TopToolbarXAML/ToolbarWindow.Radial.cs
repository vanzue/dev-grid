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

            public string Title { get; init; } = string.Empty;

            public string Category { get; init; } = string.Empty;

            public ToolbarButtonItem Item { get; init; }

            public ToolbarButton IconButton { get; init; }
        }

        private sealed class RadialVisualPalette
        {
            public required Brush HaloBrush { get; init; }

            public required Brush RingSurfaceBrush { get; init; }

            public required Brush RingOverlayBrush { get; init; }

            public required Brush RingStrokeBrush { get; init; }

            public required Brush OrbitStrokeBrush { get; init; }

            public required Brush OrbitFillBrush { get; init; }

            public required Brush CoreSurfaceBrush { get; init; }

            public required Brush CoreStrokeBrush { get; init; }

            public required Brush CoreAccentBrush { get; init; }

            public required Brush ButtonSurfaceBrush { get; init; }

            public required Brush ButtonHoverBrush { get; init; }

            public required Brush ButtonPressedBrush { get; init; }

            public required Brush ButtonStrokeBrush { get; init; }

            public required Brush ButtonGlowBrush { get; init; }

            public required Brush ButtonIconHostBrush { get; init; }

            public required Brush ButtonLabelBrush { get; init; }

            public required Brush ButtonLabelPlateBrush { get; init; }

            public required Brush ButtonCategoryBrush { get; init; }

            public required Brush IconBrush { get; init; }

            public required Brush CenterTextBrush { get; init; }

            public required Brush AccentChipBrush { get; init; }

            public required Brush AccentSmearBrush { get; init; }

            public required Color AccentAColor { get; init; }

            public required Color NotificationAccentColor { get; init; }

            public required FontFamily TextFontFamily { get; init; }
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
                Title = "Snapshot",
                Category = "Workspace",
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
                Title = "Settings",
                Category = "System",
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
                    Title = button.Button.DisplayName ?? button.Button.Name ?? "Action",
                    Category = labelPrefix,
                    Item = button,
                    IconButton = button.Button,
                });
            }
        }

        private void RefreshRadialThemeVisuals()
        {
            if (!_isRadialVisible || RadialCanvas == null || RadialCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            var entries = BuildRadialEntries();
            if (entries.Count == 0)
            {
                return;
            }

            BuildRadialVisualTree(entries, out _);
        }

        private void BuildRadialVisualTree(IReadOnlyList<RadialEntry> entries, out double diameterDip)
        {
            const double itemSize = 76;
            const double minRingRadius = 118;
            const double ringSpacing = 88;
            const double ringPadding = 84;

            var palette = CreateRadialPalette();
            var rings = CreateRings(entries.Count, minRingRadius, itemSize, ringSpacing);
            var outerRadius = rings.Count == 0 ? minRingRadius : rings[rings.Count - 1].radius;
            var center = outerRadius + (itemSize / 2d) + 34;
            diameterDip = Math.Ceiling((center * 2d) + ringPadding);

            RadialCanvas.Width = diameterDip;
            RadialCanvas.Height = diameterDip;
            RadialCanvas.Children.Clear();

            var ambientDiameter = (outerRadius * 2d) + itemSize + 132;
            var ambientHalo = new Ellipse
            {
                Width = ambientDiameter,
                Height = ambientDiameter,
                Fill = palette.HaloBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ambientHalo, center - (ambientDiameter / 2d));
            Canvas.SetTop(ambientHalo, center - (ambientDiameter / 2d));
            RadialCanvas.Children.Add(ambientHalo);

            var ringSurfaceDiameter = (outerRadius * 2d) + itemSize + 52;
            var ringSurface = new Ellipse
            {
                Width = ringSurfaceDiameter,
                Height = ringSurfaceDiameter,
                Fill = palette.RingSurfaceBrush,
                Stroke = palette.RingStrokeBrush,
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ringSurface, center - (ringSurfaceDiameter / 2d));
            Canvas.SetTop(ringSurface, center - (ringSurfaceDiameter / 2d));
            RadialCanvas.Children.Add(ringSurface);

            var ringOverlayDiameter = (outerRadius * 2d) + itemSize + 16;
            var ringOverlay = new Ellipse
            {
                Width = ringOverlayDiameter,
                Height = ringOverlayDiameter,
                Fill = palette.RingOverlayBrush,
                Stroke = palette.RingStrokeBrush,
                StrokeThickness = 0.8,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ringOverlay, center - (ringOverlayDiameter / 2d));
            Canvas.SetTop(ringOverlay, center - (ringOverlayDiameter / 2d));
            RadialCanvas.Children.Add(ringOverlay);

            var smearA = new Rectangle
            {
                Width = ringOverlayDiameter * 0.42,
                Height = 30,
                RadiusX = 13,
                RadiusY = 13,
                Fill = palette.AccentSmearBrush,
                Opacity = 0.52,
                IsHitTestVisible = false,
                RenderTransform = new RotateTransform { Angle = -24 },
            };
            Canvas.SetLeft(smearA, center + (outerRadius * 0.06));
            Canvas.SetTop(smearA, center - outerRadius - 8);
            RadialCanvas.Children.Add(smearA);

            var smearB = new Rectangle
            {
                Width = ringOverlayDiameter * 0.30,
                Height = 20,
                RadiusX = 9,
                RadiusY = 9,
                Fill = palette.AccentSmearBrush,
                Opacity = 0.34,
                IsHitTestVisible = false,
                RenderTransform = new RotateTransform { Angle = 32 },
            };
            Canvas.SetLeft(smearB, center - outerRadius - 18);
            Canvas.SetTop(smearB, center + (outerRadius * 0.34));
            RadialCanvas.Children.Add(smearB);

            var smearC = new Ellipse
            {
                Width = 108,
                Height = 62,
                Fill = palette.AccentSmearBrush,
                Opacity = 0.26,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(smearC, center + (outerRadius * 0.54));
            Canvas.SetTop(smearC, center + (outerRadius * 0.18));
            RadialCanvas.Children.Add(smearC);

            for (var ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var ringDiameter = (rings[ringIndex].radius * 2d) + itemSize + 2;
                var orbitGuide = new Ellipse
                {
                    Width = ringDiameter,
                    Height = ringDiameter,
                    Fill = palette.OrbitFillBrush,
                    Stroke = palette.OrbitStrokeBrush,
                    StrokeThickness = ringIndex == rings.Count - 1 ? 1.1 : 0.9,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(orbitGuide, center - (ringDiameter / 2d));
                Canvas.SetTop(orbitGuide, center - (ringDiameter / 2d));
                RadialCanvas.Children.Add(orbitGuide);
            }

            var centerNode = BuildCenterNode(palette);
            Canvas.SetLeft(centerNode, center - (centerNode.Width / 2d));
            Canvas.SetTop(centerNode, center - (centerNode.Height / 2d));
            RadialCanvas.Children.Add(centerNode);

            var index = 0;
            for (var ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var radius = rings[ringIndex].radius;
                var count = rings[ringIndex].count;
                var ringOffset = ringIndex % 2 == 0 ? 0d : Math.PI / count;

                for (var i = 0; i < count && index < entries.Count; i++, index++)
                {
                    var angle = (-Math.PI / 2d) + ((2d * Math.PI * i) / count) + ringOffset;
                    var x = center + (Math.Cos(angle) * radius) - (itemSize / 2d);
                    var y = center + (Math.Sin(angle) * radius) - (itemSize / 2d);
                    var button = BuildRadialButton(entries[index], itemSize, palette);
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
                var capacity = Math.Max(6, (int)Math.Floor(circumference / (itemSize + 20)));
                var count = Math.Min(remaining, capacity);
                rings.Add((radius, count));
                remaining -= count;
                radius += ringSpacing;
            }

            return rings;
        }

        private FrameworkElement BuildCenterNode(RadialVisualPalette palette)
        {
            const double size = 116;
            var host = new Grid
            {
                Width = size,
                Height = size,
                IsHitTestVisible = false,
            };

            host.Children.Add(new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2d),
                Background = palette.CoreSurfaceBrush,
                BorderBrush = palette.CoreStrokeBrush,
                BorderThickness = new Thickness(1.2),
                Shadow = new ThemeShadow(),
            });

            host.Children.Add(new Ellipse
            {
                Width = 96,
                Height = 96,
                Stroke = palette.CoreAccentBrush,
                StrokeThickness = 1.1,
                Opacity = 0.9,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            host.Children.Add(new Border
            {
                Width = 80,
                Height = 80,
                CornerRadius = new CornerRadius(40),
                Background = palette.RingOverlayBrush,
                BorderBrush = palette.CoreAccentBrush,
                BorderThickness = new Thickness(0.8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(new FontIcon
            {
                Glyph = "\uE8B7",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = palette.IconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = "Dev Grid",
                FontSize = 12,
                FontFamily = palette.TextFontFamily,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.CenterTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = "Esc to close",
                FontSize = 10,
                FontFamily = palette.TextFontFamily,
                Foreground = palette.CenterTextBrush,
                Opacity = 0.72,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            host.Children.Add(content);
            return host;
        }

        private Button BuildRadialButton(RadialEntry entry, double itemSize, RadialVisualPalette palette)
        {
            var button = new Button
            {
                Width = itemSize,
                Height = itemSize,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                UseSystemFocusVisuals = false,
                Tag = entry,
            };

            button.Resources["ButtonBackground"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

            ToolTipService.SetToolTip(button, entry.Label);

            var accentColor = entry.Kind switch
            {
                RadialEntryKind.Settings => palette.NotificationAccentColor,
                _ => palette.AccentAColor,
            };

            var glowBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.62,
                RadiusY = 0.62,
            };
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x34), Offset = 0.0 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x16), Offset = 0.52 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x00), Offset = 1.0 });

            var chipBrush = new SolidColorBrush(WithAlpha(accentColor, 0xD8));

            var root = new Grid
            {
                Width = itemSize,
                Height = itemSize,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            var scale = new ScaleTransform
            {
                ScaleX = 1,
                ScaleY = 1,
            };
            root.RenderTransform = scale;

            var glow = new Border
            {
                CornerRadius = new CornerRadius(30),
                Background = glowBrush,
                Opacity = 0.0,
                Margin = new Thickness(4),
                IsHitTestVisible = false,
            };
            root.Children.Add(glow);

            var scratch = new Rectangle
            {
                Width = itemSize * 0.78,
                Height = 14,
                RadiusX = 6,
                RadiusY = 6,
                Fill = palette.AccentSmearBrush,
                Opacity = 0.46,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, -14, 0),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = -19 },
            };
            root.Children.Add(scratch);

            var scratchEcho = new Rectangle
            {
                Width = itemSize * 0.50,
                Height = 10,
                RadiusX = 4,
                RadiusY = 4,
                Fill = palette.AccentSmearBrush,
                Opacity = 0.24,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-12, 0, 0, 10),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = 31 },
            };
            root.Children.Add(scratchEcho);

            var card = new Border
            {
                CornerRadius = new CornerRadius(28),
                Background = palette.ButtonSurfaceBrush,
                BorderBrush = palette.ButtonStrokeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 9, 10, 9),
                Shadow = new ThemeShadow(),
            };

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var accentChip = new Border
            {
                Width = 24,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = chipBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 7),
            };
            Grid.SetRow(accentChip, 0);
            content.Children.Add(accentChip);

            var body = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var iconHost = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = palette.ButtonIconHostBrush,
                BorderBrush = chipBrush,
                BorderThickness = new Thickness(0.8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconHost.Child = new ToolbarIconPresenter
            {
                Button = entry.IconButton,
                IconSize = 20,
                Foreground = _currentThemeIconColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            body.Children.Add(iconHost);
            Grid.SetRow(body, 1);
            content.Children.Add(body);

            card.Child = content;
            root.Children.Add(card);

            var hoverLabelText = new TextBlock
            {
                Text = entry.Title,
                FontSize = 11.5,
                FontFamily = palette.TextFontFamily,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.ButtonLabelBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var hoverLabel = new Border
            {
                Background = palette.ButtonLabelPlateBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -28),
                MaxWidth = Math.Max(92, itemSize + 26),
                Opacity = 0.0,
                IsHitTestVisible = false,
                Child = hoverLabelText,
            };
            root.Children.Add(hoverLabel);

            button.PointerEntered += (_, _) =>
            {
                scale.ScaleX = 1.06;
                scale.ScaleY = 1.06;
                card.Background = palette.ButtonHoverBrush;
                card.BorderBrush = chipBrush;
                glow.Opacity = 1.0;
                scratch.Opacity = 0.62;
                scratchEcho.Opacity = 0.36;
                hoverLabel.Opacity = 1.0;
            };

            button.PointerExited += (_, _) =>
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                card.Background = palette.ButtonSurfaceBrush;
                card.BorderBrush = palette.ButtonStrokeBrush;
                glow.Opacity = 0.0;
                scratch.Opacity = 0.46;
                scratchEcho.Opacity = 0.24;
                hoverLabel.Opacity = 0.0;
            };

            button.PointerPressed += (_, _) =>
            {
                scale.ScaleX = 0.98;
                scale.ScaleY = 0.98;
                card.Background = palette.ButtonPressedBrush;
                glow.Opacity = 0.72;
                scratch.Opacity = 0.76;
                scratchEcho.Opacity = 0.44;
                hoverLabel.Opacity = 1.0;
            };

            button.PointerReleased += (_, _) =>
            {
                scale.ScaleX = 1.06;
                scale.ScaleY = 1.06;
                card.Background = palette.ButtonHoverBrush;
                glow.Opacity = 1.0;
                scratch.Opacity = 0.62;
                scratchEcho.Opacity = 0.36;
                hoverLabel.Opacity = 1.0;
            };

            button.Content = root;
            button.Click += OnRadialButtonClick;
            return button;
        }

        private RadialVisualPalette CreateRadialPalette()
        {
            var theme = _vm?.Theme ?? ToolbarTheme.WarmFrosted;
            var tokens = _currentThemeTokens;
            if (tokens == null)
            {
                tokens = GetThemeTokens(theme);
                ApplySaturationProfile(theme, tokens);
                EnsureInteractiveContrast(tokens);
                _currentThemeTokens = tokens;
                _currentThemeIconColor = GetNeutralIconColor(tokens);
            }

            EnsureAccentPair(theme, tokens);

            var blendedAccent = BlendRgb(_accentA, _accentB, 0.5);
            var innerSurface = BlendRgb(tokens.BackgroundInner, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.10);
            var buttonSurface = BlendRgb(tokens.BackgroundInner, tokens.BackgroundMiddle, 0.30);
            var buttonHover = BlendRgb(tokens.ButtonHover, tokens.BackgroundInner, 0.22);
            var buttonPressed = BlendRgb(tokens.ButtonPressed, tokens.BackgroundOuter, 0.16);
            var orbitStroke = BlendRgb(tokens.Separator, blendedAccent, 0.32);

            var haloBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x18), Offset = 0.0 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x22), Offset = 0.28 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x14), Offset = 0.56 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.BackgroundOuter, 0x00), Offset = 1.0 });

            var ringSurfaceBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.46),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.30),
                RadiusX = 0.68,
                RadiusY = 0.68,
            };
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(innerSurface, 0xF0), Offset = 0.0 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(tokens.BackgroundMiddle, _accentA, 0.08), 0xDD), Offset = 0.48 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.BackgroundOuter, 0xCE), Offset = 1.0 });

            var ringOverlayBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.38),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.26),
                RadiusX = 0.74,
                RadiusY = 0.74,
            };
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x24), Offset = 0.0 });
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x16), Offset = 0.34 });
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.BackgroundOuter, 0x14), Offset = 1.0 });

            var coreSurfaceBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.34),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.18),
                RadiusX = 0.74,
                RadiusY = 0.74,
            };
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(tokens.BackgroundInner, _accentA, 0.10), 0xF2), Offset = 0.0 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(tokens.BackgroundMiddle, _accentB, 0.12), 0xE4), Offset = 0.48 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(tokens.BackgroundOuter, blendedAccent, 0.12), 0xD8), Offset = 1.0 });

            var buttonSurfaceBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.15, 0.0),
                EndPoint = new Windows.Foundation.Point(0.85, 1.0),
            };
            buttonSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(buttonSurface, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.10), 0xF2), Offset = 0.0 });
            buttonSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(buttonSurface, 0xEC), Offset = 1.0 });

            var buttonHoverBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.1, 0.0),
                EndPoint = new Windows.Foundation.Point(0.9, 1.0),
            };
            buttonHoverBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(buttonHover, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.12), 0xF8), Offset = 0.0 });
            buttonHoverBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(buttonHover, _accentA, 0.06), 0xF1), Offset = 1.0 });

            var buttonPressedBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.25, 0.0),
                EndPoint = new Windows.Foundation.Point(0.75, 1.0),
            };
            buttonPressedBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(buttonPressed, 0xF4), Offset = 0.0 });
            buttonPressedBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(buttonPressed, tokens.BackgroundOuter, 0.18), 0xED), Offset = 1.0 });

            var accentSmearBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.0, 0.5),
                EndPoint = new Windows.Foundation.Point(1.0, 0.5),
            };
            accentSmearBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x00), Offset = 0.0 });
            accentSmearBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0xC8), Offset = 0.30 });
            accentSmearBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.NotificationAccent, 0x7A), Offset = 0.68 });
            accentSmearBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.NotificationAccent, 0x00), Offset = 1.0 });

            return new RadialVisualPalette
            {
                HaloBrush = haloBrush,
                RingSurfaceBrush = ringSurfaceBrush,
                RingOverlayBrush = ringOverlayBrush,
                RingStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Border, _accentA, 0.08), 0xAE)),
                OrbitStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(orbitStroke, blendedAccent, 0.28), 0x88)),
                OrbitFillBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.BackgroundMiddle, _accentA, 0.16), 0x14)),
                CoreSurfaceBrush = coreSurfaceBrush,
                CoreStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Border, _accentA, 0.20), 0xC8)),
                CoreAccentBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.HighlightA, _accentB, 0.36), 0x96)),
                ButtonSurfaceBrush = buttonSurfaceBrush,
                ButtonHoverBrush = buttonHoverBrush,
                ButtonPressedBrush = buttonPressedBrush,
                ButtonStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Border, tokens.Label, 0.06), 0xB6)),
                ButtonGlowBrush = new SolidColorBrush(WithAlpha(_accentA, 0x22)),
                ButtonIconHostBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.BackgroundOuter, tokens.BackgroundInner, 0.18), 0x68)),
                ButtonLabelBrush = new SolidColorBrush(WithAlpha(tokens.Label, 0xFA)),
                ButtonLabelPlateBrush = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0xDC)),
                ButtonCategoryBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Label, _accentB, 0.22), 0xC6)),
                IconBrush = new SolidColorBrush(_currentThemeIconColor),
                CenterTextBrush = new SolidColorBrush(WithAlpha(tokens.Label, 0xEA)),
                AccentChipBrush = new SolidColorBrush(WithAlpha(_accentA, 0xC8)),
                AccentSmearBrush = accentSmearBrush,
                AccentAColor = _accentA,
                NotificationAccentColor = tokens.NotificationAccent,
                TextFontFamily = new FontFamily(tokens.FontFamily),
            };
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
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
