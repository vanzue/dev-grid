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
        private bool _lastEscapeDown;
        private bool _lastMouseDown;
        private long _lastRadialHotKeyTriggerTick;
        private long _radialShownTick;

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

            public required Brush AccentSmearSoftBrush { get; init; }

            public required IReadOnlyList<Color> ButtonAccentColors { get; init; }

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
                HideRadialMenu();
                return;
            }

            CloseSettingsWindow();
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
            _lastEscapeDown = false;
            _lastMouseDown = false;

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

            const int VkEscape = 0x1B;
            var escapeDown = (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
            if (_isRadialVisible && escapeDown && !_lastEscapeDown)
            {
                TryEnqueueRadialDismiss();
            }

            _lastEscapeDown = escapeDown;

            const int VkLeftButton = 0x01;
            const int VkRightButton = 0x02;
            var mouseDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;
            if (_isRadialVisible && mouseDown && !_lastMouseDown)
            {
                var elapsedSinceShow = Environment.TickCount64 - _radialShownTick;
                if (elapsedSinceShow > 250 && IsCursorOutsideRadialWindow())
                {
                    TryEnqueueRadialDismiss();
                }
            }

            _lastMouseDown = mouseDown;
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

        private void TryEnqueueRadialDismiss()
        {
            _ = DispatcherQueue?.TryEnqueue(HideRadialMenu);
        }

        private bool IsCursorOutsideRadialWindow()
        {
            if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect) || !GetCursorPos(out var cursor))
            {
                return false;
            }

            return cursor.X < rect.Left ||
                   cursor.X >= rect.Right ||
                   cursor.Y < rect.Top ||
                   cursor.Y >= rect.Bottom;
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
                    _radialShownTick = Environment.TickCount64;

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

            void AddShard(double width, double height, double opacity, double rotation, double centerX, double centerY, Color color, int seed)
            {
                var blob = CreateOrganicSmear(width, height, new SolidColorBrush(WithAlpha(color, 0xD8)), opacity, rotation, seed);
                Canvas.SetLeft(blob, centerX - (width / 2d));
                Canvas.SetTop(blob, centerY - (height / 2d));
                RadialCanvas.Children.Add(blob);
            }

            AddShard(ringOverlayDiameter * 0.52, ringOverlayDiameter * 0.24, 0.22, -18, center + (outerRadius * 0.26), center - (outerRadius * 0.52), palette.ButtonAccentColors[1], 4172);
            AddShard(ringOverlayDiameter * 0.38, ringOverlayDiameter * 0.18, 0.18, 24, center - (outerRadius * 0.54), center + (outerRadius * 0.30), palette.ButtonAccentColors[3], 9051);
            AddShard(ringOverlayDiameter * 0.30, ringOverlayDiameter * 0.20, 0.16, 8, center + (outerRadius * 0.52), center + (outerRadius * 0.36), palette.ButtonAccentColors[5], 6618);

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
                    var button = BuildRadialButton(entries[index], itemSize, palette, index);
                    Canvas.SetLeft(button, x);
                    Canvas.SetTop(button, y);
                    RadialCanvas.Children.Add(button);
                }
            }
        }

        private static Microsoft.UI.Xaml.Shapes.Path CreateOrganicSmear(
            double width, double height, Brush fill, double opacity, double rotation, int seed)
        {
            var rnd = new Random(seed);
            var rx = width / 2d;
            var ry = height / 2d;
            var cx = rx;
            var cy = ry;
            const int points = 9;
            var outline = new Windows.Foundation.Point[points];
            for (var i = 0; i < points; i++)
            {
                var a = ((2d * Math.PI * i) / points) + (rnd.NextDouble() * 0.22);
                var jitter = 0.5 + (rnd.NextDouble() * 0.62);
                outline[i] = new Windows.Foundation.Point(
                    cx + (Math.Cos(a) * rx * jitter),
                    cy + (Math.Sin(a) * ry * jitter));
            }

            static Windows.Foundation.Point Mid(Windows.Foundation.Point p, Windows.Foundation.Point q)
                => new((p.X + q.X) / 2d, (p.Y + q.Y) / 2d);

            var figure = new PathFigure
            {
                IsClosed = true,
                IsFilled = true,
                StartPoint = Mid(outline[points - 1], outline[0]),
            };
            for (var i = 0; i < points; i++)
            {
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = outline[i],
                    Point2 = Mid(outline[i], outline[(i + 1) % points]),
                });
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Fill = fill,
                Opacity = opacity,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = rotation },
            };
        }

        private static void AnimateDouble(DependencyObject target, string property, double to, double milliseconds, bool dependent)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EnableDependentAnimation = dependent,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
            storyboard.Begin();
        }

        private static Color GetRadialTileAccent(RadialVisualPalette palette, int index, RadialEntryKind kind)
        {
            if (kind == RadialEntryKind.Settings)
            {
                return Color.FromArgb(0xFF, 0xFF, 0x3B, 0x5C);
            }

            if (kind == RadialEntryKind.Snapshot)
            {
                return Color.FromArgb(0xFF, 0xFF, 0xB0, 0x00);
            }

            var colors = palette.ButtonAccentColors;
            return colors[((index % colors.Count) + colors.Count) % colors.Count];
        }

        private static double GetRadialTileRotation(int index)
        {
            var rotations = new[] { -10d, 7d, -5d, 12d, -13d, 4d, 9d, -8d };
            return rotations[((index % rotations.Length) + rotations.Length) % rotations.Length];
        }

        private static Brush CreateBoldTileBrush(Color accent, Color secondary, bool hover, bool pressed)
        {
            var mix = pressed ? 0.34 : hover ? 0.16 : 0.0;
            var top = BlendRgb(accent, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), hover ? 0.10 : 0.02);
            var bottom = BlendRgb(secondary, Color.FromArgb(0xFF, 0x03, 0x05, 0x0A), mix);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.0, 0.08),
                EndPoint = new Windows.Foundation.Point(1.0, 0.92),
            };
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(top, pressed ? (byte)0xE8 : (byte)0xFA), Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(accent, secondary, 0.42), pressed ? (byte)0xE2 : (byte)0xF0), Offset = 0.48 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(bottom, pressed ? (byte)0xEE : (byte)0xF6), Offset = 1.0 });
            return brush;
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

        private Button BuildRadialButton(RadialEntry entry, double itemSize, RadialVisualPalette palette, int index)
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

            var accentColor = GetRadialTileAccent(palette, index, entry.Kind);
            var secondaryColor = GetRadialTileAccent(palette, index + 3, entry.Kind);
            var tileBrush = CreateBoldTileBrush(accentColor, secondaryColor, hover: false, pressed: false);
            var tileHoverBrush = CreateBoldTileBrush(accentColor, secondaryColor, hover: true, pressed: false);
            var tilePressedBrush = CreateBoldTileBrush(accentColor, secondaryColor, hover: false, pressed: true);

            var glowBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.72,
                RadiusY = 0.72,
            };
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x78), Offset = 0.0 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(secondaryColor, 0x28), Offset = 0.54 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x00), Offset = 1.0 });

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

            var tile = CreateOrganicSmear(itemSize * 0.92, itemSize * 0.92, tileBrush, 1.0, GetRadialTileRotation(index), 7100 + index);
            tile.HorizontalAlignment = HorizontalAlignment.Center;
            tile.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(tile);

            var slash = new Border
            {
                Width = itemSize * 0.54,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x56)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 4, 0),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = -18 },
            };
            root.Children.Add(slash);

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Margin = new Thickness(8, 8, 8, 7);

            var category = new TextBlock
            {
                Text = entry.Category?.Length > 10 ? entry.Category[..10].ToUpperInvariant() : (entry.Category ?? string.Empty).ToUpperInvariant(),
                FontSize = 7.5,
                FontFamily = palette.TextFontFamily,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0xCC)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                CharacterSpacing = 110,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 2),
            };
            Grid.SetRow(category, 0);
            content.Children.Add(category);

            var body = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var iconHost = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0x03, 0x05, 0x0A), 0x52)),
                BorderBrush = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x6C)),
                BorderThickness = new Thickness(1.1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconHost.Child = new ToolbarIconPresenter
            {
                Button = entry.IconButton,
                IconSize = 20,
                Foreground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            body.Children.Add(iconHost);
            Grid.SetRow(body, 1);
            content.Children.Add(body);

            var titlePlate = new Border
            {
                Background = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0x04, 0x06, 0x0D), 0x9A)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 2, 5, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = itemSize - 10,
            };
            titlePlate.Child = new TextBlock
            {
                Text = entry.Title,
                FontSize = 8.8,
                FontFamily = palette.TextFontFamily,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(titlePlate, 2);
            content.Children.Add(titlePlate);
            root.Children.Add(content);

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
                tile.Fill = tileHoverBrush;
                AnimateDouble(scale, "ScaleX", 1.05, 200, true);
                AnimateDouble(scale, "ScaleY", 1.05, 200, true);
                AnimateDouble(glow, "Opacity", 0.9, 220, false);
                AnimateDouble(slash, "Opacity", 1.0, 180, false);
                AnimateDouble(hoverLabel, "Opacity", 1.0, 200, false);
            };

            button.PointerExited += (_, _) =>
            {
                tile.Fill = tileBrush;
                AnimateDouble(scale, "ScaleX", 1.0, 260, true);
                AnimateDouble(scale, "ScaleY", 1.0, 260, true);
                AnimateDouble(glow, "Opacity", 0.0, 280, false);
                AnimateDouble(slash, "Opacity", 0.72, 220, false);
                AnimateDouble(hoverLabel, "Opacity", 0.0, 220, false);
            };

            button.PointerPressed += (_, _) =>
            {
                tile.Fill = tilePressedBrush;
                AnimateDouble(scale, "ScaleX", 0.98, 120, true);
                AnimateDouble(scale, "ScaleY", 0.98, 120, true);
                AnimateDouble(glow, "Opacity", 0.72, 140, false);
                AnimateDouble(slash, "Opacity", 0.92, 120, false);
                AnimateDouble(hoverLabel, "Opacity", 1.0, 120, false);
            };

            button.PointerReleased += (_, _) =>
            {
                tile.Fill = tileHoverBrush;
                AnimateDouble(scale, "ScaleX", 1.05, 200, true);
                AnimateDouble(scale, "ScaleY", 1.05, 200, true);
                AnimateDouble(glow, "Opacity", 0.9, 220, false);
                AnimateDouble(slash, "Opacity", 1.0, 160, false);
                AnimateDouble(hoverLabel, "Opacity", 1.0, 200, false);
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

            var boldTileColors = new[]
            {
                Color.FromArgb(0xFF, 0x20, 0x5B, 0xFF),
                Color.FromArgb(0xFF, 0xD6, 0x16, 0xFF),
                Color.FromArgb(0xFF, 0x00, 0xC2, 0x8C),
                Color.FromArgb(0xFF, 0xFF, 0x4D, 0x00),
                Color.FromArgb(0xFF, 0x70, 0x35, 0xFF),
                Color.FromArgb(0xFF, 0x00, 0x8C, 0xFF),
                Color.FromArgb(0xFF, 0xE8, 0x12, 0x5B),
                Color.FromArgb(0xFF, 0x12, 0xB8, 0x2F),
            };
            var buttonSurface = BlendRgb(tokens.BackgroundInner, tokens.BackgroundMiddle, 0.30);
            var buttonHover = BlendRgb(tokens.ButtonHover, tokens.BackgroundInner, 0.22);
            var buttonPressed = BlendRgb(tokens.ButtonPressed, tokens.BackgroundOuter, 0.16);

            var haloBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(boldTileColors[1], 0x2C), Offset = 0.0 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(boldTileColors[5], 0x22), Offset = 0.42 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(boldTileColors[6], 0x18), Offset = 0.68 });
            haloBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.BackgroundOuter, 0x00), Offset = 1.0 });

            var ringSurfaceBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.46),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.30),
                RadiusX = 0.68,
                RadiusY = 0.68,
            };
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xDE, 0x08, 0x0A, 0x12), Offset = 0.0 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xD4, 0x0D, 0x10, 0x1E), Offset = 0.48 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xC8, 0x03, 0x04, 0x08), Offset = 1.0 });

            var ringOverlayBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.38),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.26),
                RadiusX = 0.74,
                RadiusY = 0.74,
            };
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), Offset = 0.0 });
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(boldTileColors[0], 0x10), Offset = 0.46 });
            ringOverlayBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 1.0 });

            var coreSurfaceBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.34),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.18),
                RadiusX = 0.74,
                RadiusY = 0.74,
            };
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xF0, 0x12, 0x15, 0x24), Offset = 0.0 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xE8, 0x08, 0x0A, 0x12), Offset = 0.58 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xDC, 0x03, 0x04, 0x08), Offset = 1.0 });

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

            var accentSmearSoftBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.42, 0.40),
                RadiusX = 0.62,
                RadiusY = 0.62,
                SpreadMethod = GradientSpreadMethod.Pad,
            };
            accentSmearSoftBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(_accentA, tokens.NotificationAccent, 0.18), 0xA8), Offset = 0.0 });
            accentSmearSoftBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accentA, 0x52), Offset = 0.46 });
            accentSmearSoftBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.NotificationAccent, 0x22), Offset = 0.74 });
            accentSmearSoftBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(tokens.NotificationAccent, 0x00), Offset = 1.0 });

            return new RadialVisualPalette
            {
                HaloBrush = haloBrush,
                RingSurfaceBrush = ringSurfaceBrush,
                RingOverlayBrush = ringOverlayBrush,
                RingStrokeBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                OrbitStrokeBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                OrbitFillBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                CoreSurfaceBrush = coreSurfaceBrush,
                CoreStrokeBrush = new SolidColorBrush(Color.FromArgb(0x82, 0xFF, 0xFF, 0xFF)),
                CoreAccentBrush = new SolidColorBrush(WithAlpha(boldTileColors[1], 0x96)),
                ButtonSurfaceBrush = buttonSurfaceBrush,
                ButtonHoverBrush = buttonHoverBrush,
                ButtonPressedBrush = buttonPressedBrush,
                ButtonStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Border, tokens.Label, 0.06), 0xB6)),
                ButtonGlowBrush = new SolidColorBrush(WithAlpha(_accentA, 0x22)),
                ButtonIconHostBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.BackgroundOuter, tokens.BackgroundInner, 0.18), 0x68)),
                ButtonLabelBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                ButtonLabelPlateBrush = new SolidColorBrush(Color.FromArgb(0xE8, 0x04, 0x06, 0x0D)),
                ButtonCategoryBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                IconBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                CenterTextBrush = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)),
                AccentChipBrush = new SolidColorBrush(WithAlpha(_accentA, 0xC8)),
                AccentSmearBrush = accentSmearBrush,
                AccentSmearSoftBrush = accentSmearSoftBrush,
                ButtonAccentColors = boldTileColors,
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

        private void OnRadialCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isRadialVisible && ReferenceEquals(e.OriginalSource, RadialCanvas))
            {
                HideRadialMenu();
                e.Handled = true;
            }
        }
    }
}
