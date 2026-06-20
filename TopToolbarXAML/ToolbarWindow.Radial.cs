// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
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
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkSpace = 0x20;

        private ToolbarDisplayMode _currentDisplayMode = ToolbarDisplayMode.TopBar;
        private bool _topBarEnabled = true;
        private bool _radialMenuEnabled = true;
        private bool _radialHotKeyRegistered;
        private bool _isRadialVisible;
        private bool _isShowingRadial;
        private System.Timers.Timer _radialHotKeyPollTimer;
        private bool _radialFallbackPolling;
        private bool _lastRadialHotKeyComboDown;
        private bool _lastEscapeDown;
        private bool _lastMouseDown;
        private long _lastRadialHotKeyTriggerTick;
        private long _radialShownTick;
        private int _radialCenterScreenX;
        private int _radialCenterScreenY;
        private int _radialSizePx;

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

            public required Color SnapshotAccentColor { get; init; }

            public required Color SettingsAccentColor { get; init; }

            public required Color AccentAColor { get; init; }

            public required Color NotificationAccentColor { get; init; }

            public required FontFamily TextFontFamily { get; init; }
        }

        private sealed class RadialThemePalette
        {
            public required Color[] TileColors { get; init; }

            public required Color SurfaceInner { get; init; }

            public required Color SurfaceMiddle { get; init; }

            public required Color SurfaceOuter { get; init; }

            public required Color Stroke { get; init; }

            public required Color CoreAccent { get; init; }

            public required Color LabelPlate { get; init; }

            public required Color Label { get; init; }

            public required Color Category { get; init; }

            public required Color SnapshotAccent { get; init; }

            public required Color SettingsAccent { get; init; }
        }

        private void ApplyDisplayMode(ToolbarDisplayMode mode)
        {
            ApplyInvocationModes(mode == ToolbarDisplayMode.TopBar, mode == ToolbarDisplayMode.RadialMenu, mode);
        }

        private void ApplyInvocationModes(bool topBarEnabled, bool radialMenuEnabled, ToolbarDisplayMode legacyMode)
        {
            _currentDisplayMode = legacyMode;
            _topBarEnabled = topBarEnabled;
            _radialMenuEnabled = radialMenuEnabled;

            if (_radialMenuEnabled)
            {
                EnsureRadialHotKey();
                StartRadialHotKeyFallbackPolling();
            }
            else
            {
                HideRadialMenu();
                UnregisterRadialHotKey();
            }

            if (_topBarEnabled)
            {
                ToolbarContainer.Visibility = _isRadialVisible ? Visibility.Collapsed : Visibility.Visible;
                StartMonitoring();
            }
            else
            {
                StopMonitoring();
                HideToolbar();
                ToolbarContainer.Visibility = Visibility.Collapsed;
            }

            if (!_topBarEnabled && _radialMenuEnabled && !_isRadialVisible)
            {
                ParkRadialHostWindow();
            }
        }

        private void EnsureRadialHotKey()
        {
            if (_radialHotKeyRegistered || _hwnd == IntPtr.Zero)
            {
                return;
            }

            var ok = RegisterHotKey(_hwnd, RadialHotKeyId, ModControl | ModShift | ModNoRepeat, VkSpace);
            if (!ok)
            {
                AppLogger.LogWarning("RadialMenu: failed to register Ctrl+Shift+Space hotkey.");
                return;
            }

            _radialHotKeyRegistered = true;
            AppLogger.LogInfo("RadialMenu: Ctrl+Shift+Space hotkey registered.");
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

            if (!_radialMenuEnabled)
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
            _lastRadialHotKeyComboDown = false;
            _radialFallbackPolling = true;
            _radialHotKeyPollTimer.Start();
            AppLogger.LogInfo("RadialMenu: Ctrl+Shift+Space polling enabled.");
        }

        private void StopRadialHotKeyFallbackPolling(bool disposeTimer)
        {
            _radialFallbackPolling = false;
            _lastRadialHotKeyComboDown = false;
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
            if (!_radialMenuEnabled)
            {
                return;
            }

            const int VkControl = 0x11;
            const int VkShift = 0x10;
            const int VkSpaceInt = 0x20;
            var controlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
            var shiftDown = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
            var spaceDown = (GetAsyncKeyState(VkSpaceInt) & 0x8000) != 0;
            var comboDown = controlDown && shiftDown && spaceDown;

            if (comboDown && !_lastRadialHotKeyComboDown)
            {
                TryEnqueueRadialHotKeyPress();
            }

            _lastRadialHotKeyComboDown = comboDown;

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

                    _radialCenterScreenX = targetX + (sizePx / 2);
                    _radialCenterScreenY = targetY + (sizePx / 2);
                    _radialSizePx = sizePx;

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
            _lastEscapeDown = false;
            _lastMouseDown = false;
            RadialCanvas.Visibility = Visibility.Collapsed;
            ParkRadialHostWindow();
        }

        private void ParkRadialHostWindow()
        {
            if (!_radialMenuEnabled)
            {
                AppWindow.Hide();
                return;
            }

            if (_topBarEnabled)
            {
                ToolbarContainer.Visibility = Visibility.Visible;
                ResizeToContent();
                PositionAtTopCenter();
                AppWindow.Hide();
                _isVisible = false;
                UpdateToastWindowAnchor();
                return;
            }

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea?.WorkArea ?? new Windows.Graphics.RectInt32(0, 0, 1, 1);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
            AppWindow.Move(new Windows.Graphics.PointInt32(workArea.X - 2, workArea.Y - 2));
            AppWindow.Show(false);
        }

        private List<RadialEntry> BuildRadialEntries()
        {
            var entries = new List<RadialEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in ItemsViewModel.AllGroups)
            {
                var labelPrefix = IsWorkspaceGroup(group)
                    ? "Workspace"
                    : (group?.Group?.Name ?? "Action");
                AppendGroupButtons(entries, seen, group, labelPrefix);
            }

            return entries.Take(8).ToList();
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

            bool isWorkspace = IsWorkspaceGroup(group);

            foreach (var button in group.RingButtons)
            {
                if (button?.Button == null || !button.IsEnabled)
                {
                    continue;
                }

                // Ring membership is driven by the unified pin/surface model.
                if ((button.Button.Surfaces & ActionSurfaces.Ring) == 0)
                {
                    continue;
                }

                // Workspaces only show their "hot" (live) instances in the ring; cold workspaces are dimmed.
                if (isWorkspace && button.Button.IsDimmed)
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
                return palette.SettingsAccentColor;
            }

            if (kind == RadialEntryKind.Snapshot)
            {
                return palette.SnapshotAccentColor;
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
            const double size = 96;
            var host = new Grid
            {
                Width = size,
                Height = size,
                IsHitTestVisible = false,
            };

            var coreAuraBrush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.45, 0.42),
                RadiusX = 0.55,
                RadiusY = 0.55,
            };
            coreAuraBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[0], 0x58), Offset = 0.0 });
            coreAuraBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[2], 0x28), Offset = 0.56 });
            coreAuraBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[0], 0x00), Offset = 1.0 });

            host.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Fill = coreAuraBrush,
                Opacity = 0.75,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var coreBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.12, 0.04),
                EndPoint = new Windows.Foundation.Point(0.92, 1.0),
            };
            coreBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[1], 0xEC), Offset = 0.0 });
            coreBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[4], 0xD4), Offset = 0.58 });
            coreBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(palette.ButtonAccentColors[6], 0xC0), Offset = 1.0 });

            var core = CreateOrganicSmear(46, 42, coreBrush, 0.96, -12, 12101);
            core.HorizontalAlignment = HorizontalAlignment.Center;
            core.VerticalAlignment = VerticalAlignment.Center;
            core.Margin = new Thickness(1, -2, 0, 0);
            host.Children.Add(core);

            var shardA = CreateOrganicSmear(18, 10, new SolidColorBrush(WithAlpha(palette.ButtonAccentColors[3], 0xD8)), 0.82, 24, 12102);
            shardA.HorizontalAlignment = HorizontalAlignment.Right;
            shardA.VerticalAlignment = VerticalAlignment.Top;
            shardA.Margin = new Thickness(0, 18, 18, 0);
            host.Children.Add(shardA);

            var shardB = CreateOrganicSmear(13, 20, new SolidColorBrush(WithAlpha(palette.ButtonAccentColors[5], 0xBA)), 0.72, -18, 12103);
            shardB.HorizontalAlignment = HorizontalAlignment.Left;
            shardB.VerticalAlignment = VerticalAlignment.Bottom;
            shardB.Margin = new Thickness(22, 0, 0, 17);
            host.Children.Add(shardB);

            var shardC = CreateOrganicSmear(10, 10, new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x88)), 0.6, 8, 12104);
            shardC.HorizontalAlignment = HorizontalAlignment.Right;
            shardC.VerticalAlignment = VerticalAlignment.Bottom;
            shardC.Margin = new Thickness(0, 0, 26, 24);
            host.Children.Add(shardC);

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

            var liquidBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.0, 0.15),
                EndPoint = new Windows.Foundation.Point(1.0, 0.88),
            };
            liquidBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x92), Offset = 0.0 });
            liquidBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(secondaryColor, 0xA6), Offset = 0.46 });
            liquidBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(accentColor, 0x46), Offset = 1.0 });

            var tile = CreateOrganicSmear(itemSize * 0.92, itemSize * 0.92, tileBrush, 1.0, GetRadialTileRotation(index), 7100 + index);
            tile.HorizontalAlignment = HorizontalAlignment.Center;
            tile.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(tile);

            var liquidBack = CreateOrganicSmear(itemSize * 0.78, itemSize * 0.46, liquidBrush, 0.28, -24 + (index % 3 * 9), 9100 + index);
            liquidBack.HorizontalAlignment = HorizontalAlignment.Center;
            liquidBack.VerticalAlignment = VerticalAlignment.Center;
            liquidBack.Margin = new Thickness(-8, -2, 0, 0);
            root.Children.Add(liquidBack);

            var liquidDrop = CreateOrganicSmear(itemSize * 0.34, itemSize * 0.26, new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0xB8)), 0.18, 18, 9500 + index);
            liquidDrop.HorizontalAlignment = HorizontalAlignment.Left;
            liquidDrop.VerticalAlignment = VerticalAlignment.Bottom;
            liquidDrop.Margin = new Thickness(12, 0, 0, 13);
            root.Children.Add(liquidDrop);

            var slash = new Border
            {
                Width = itemSize * 0.54,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(WithAlpha(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0x56)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 4, 0),
                Opacity = 0.54,
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
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            var iconScale = new ScaleTransform
            {
                ScaleX = 1,
                ScaleY = 1,
            };
            body.RenderTransform = iconScale;

            var icon = new ToolbarIconPresenter
            {
                Button = entry.IconButton,
                IconSize = 28,
                Foreground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            body.Children.Add(icon);
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
                AnimateDouble(scale, "ScaleX", 1.03, 200, true);
                AnimateDouble(scale, "ScaleY", 1.03, 200, true);
                AnimateDouble(iconScale, "ScaleX", 1.9, 190, true);
                AnimateDouble(iconScale, "ScaleY", 1.9, 190, true);
                AnimateDouble(category, "Opacity", 0.18, 150, false);
                AnimateDouble(titlePlate, "Opacity", 0.26, 150, false);
                AnimateDouble(liquidBack, "Opacity", 0.74, 220, false);
                AnimateDouble(liquidDrop, "Opacity", 0.58, 240, false);
                AnimateDouble(slash, "Opacity", 1.0, 180, false);
                AnimateDouble(hoverLabel, "Opacity", 1.0, 200, false);
            };

            button.PointerExited += (_, _) =>
            {
                tile.Fill = tileBrush;
                AnimateDouble(scale, "ScaleX", 1.0, 260, true);
                AnimateDouble(scale, "ScaleY", 1.0, 260, true);
                AnimateDouble(iconScale, "ScaleX", 1.0, 210, true);
                AnimateDouble(iconScale, "ScaleY", 1.0, 210, true);
                AnimateDouble(category, "Opacity", 1.0, 180, false);
                AnimateDouble(titlePlate, "Opacity", 1.0, 180, false);
                AnimateDouble(liquidBack, "Opacity", 0.28, 280, false);
                AnimateDouble(liquidDrop, "Opacity", 0.18, 260, false);
                AnimateDouble(slash, "Opacity", 0.72, 220, false);
                AnimateDouble(hoverLabel, "Opacity", 0.0, 220, false);
            };

            button.PointerPressed += (_, _) =>
            {
                tile.Fill = tilePressedBrush;
                AnimateDouble(scale, "ScaleX", 0.98, 120, true);
                AnimateDouble(scale, "ScaleY", 0.98, 120, true);
                AnimateDouble(iconScale, "ScaleX", 1.55, 100, true);
                AnimateDouble(iconScale, "ScaleY", 1.55, 100, true);
                AnimateDouble(category, "Opacity", 0.12, 90, false);
                AnimateDouble(titlePlate, "Opacity", 0.20, 90, false);
                AnimateDouble(liquidBack, "Opacity", 0.82, 140, false);
                AnimateDouble(liquidDrop, "Opacity", 0.68, 140, false);
                AnimateDouble(slash, "Opacity", 0.92, 120, false);
                AnimateDouble(hoverLabel, "Opacity", 1.0, 120, false);
            };

            button.PointerReleased += (_, _) =>
            {
                tile.Fill = tileHoverBrush;
                AnimateDouble(scale, "ScaleX", 1.03, 200, true);
                AnimateDouble(scale, "ScaleY", 1.03, 200, true);
                AnimateDouble(iconScale, "ScaleX", 1.9, 190, true);
                AnimateDouble(iconScale, "ScaleY", 1.9, 190, true);
                AnimateDouble(category, "Opacity", 0.18, 150, false);
                AnimateDouble(titlePlate, "Opacity", 0.26, 150, false);
                AnimateDouble(liquidBack, "Opacity", 0.74, 220, false);
                AnimateDouble(liquidDrop, "Opacity", 0.58, 220, false);
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

            var radialTheme = GetRadialThemePalette(theme);
            var boldTileColors = radialTheme.TileColors;
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
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = radialTheme.SurfaceInner, Offset = 0.0 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = radialTheme.SurfaceMiddle, Offset = 0.48 });
            ringSurfaceBrush.GradientStops.Add(new GradientStop { Color = radialTheme.SurfaceOuter, Offset = 1.0 });

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
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(BlendRgb(radialTheme.SurfaceInner, radialTheme.CoreAccent, 0.12), 0xF0), Offset = 0.0 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(radialTheme.SurfaceMiddle, 0xE8), Offset = 0.58 });
            coreSurfaceBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(radialTheme.SurfaceOuter, 0xDC), Offset = 1.0 });

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
                RingStrokeBrush = new SolidColorBrush(radialTheme.Stroke),
                OrbitStrokeBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                OrbitFillBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                CoreSurfaceBrush = coreSurfaceBrush,
                CoreStrokeBrush = new SolidColorBrush(WithAlpha(radialTheme.Stroke, 0x92)),
                CoreAccentBrush = new SolidColorBrush(WithAlpha(radialTheme.CoreAccent, 0x96)),
                ButtonSurfaceBrush = buttonSurfaceBrush,
                ButtonHoverBrush = buttonHoverBrush,
                ButtonPressedBrush = buttonPressedBrush,
                ButtonStrokeBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.Border, tokens.Label, 0.06), 0xB6)),
                ButtonGlowBrush = new SolidColorBrush(WithAlpha(_accentA, 0x22)),
                ButtonIconHostBrush = new SolidColorBrush(WithAlpha(BlendRgb(tokens.BackgroundOuter, tokens.BackgroundInner, 0.18), 0x68)),
                ButtonLabelBrush = new SolidColorBrush(radialTheme.Label),
                ButtonLabelPlateBrush = new SolidColorBrush(radialTheme.LabelPlate),
                ButtonCategoryBrush = new SolidColorBrush(radialTheme.Category),
                IconBrush = new SolidColorBrush(radialTheme.Label),
                CenterTextBrush = new SolidColorBrush(WithAlpha(radialTheme.Label, 0xEA)),
                AccentChipBrush = new SolidColorBrush(WithAlpha(_accentA, 0xC8)),
                AccentSmearBrush = accentSmearBrush,
                AccentSmearSoftBrush = accentSmearSoftBrush,
                ButtonAccentColors = boldTileColors,
                SnapshotAccentColor = radialTheme.SnapshotAccent,
                SettingsAccentColor = radialTheme.SettingsAccent,
                AccentAColor = _accentA,
                NotificationAccentColor = tokens.NotificationAccent,
                TextFontFamily = new FontFamily(tokens.FontFamily),
            };
        }

        private static RadialThemePalette GetRadialThemePalette(ToolbarTheme theme)
        {
            static Color C(string hex) => Hex(hex);
            static Color A(string hex, byte alpha) => Hex(hex, alpha);

            return theme switch
            {
                ToolbarTheme.ArcticGlass => new RadialThemePalette
                {
                    TileColors = new[] { C("00A6FF"), C("6D5BFF"), C("00E5FF"), C("B9F2FF"), C("3D7CFF"), C("00FFC8"), C("D7E7FF"), C("7A5CFF") },
                    SurfaceInner = A("071625", 0xDE),
                    SurfaceMiddle = A("06101C", 0xD6),
                    SurfaceOuter = A("02070D", 0xC8),
                    Stroke = A("B9F2FF", 0x76),
                    CoreAccent = C("00E5FF"),
                    LabelPlate = A("020914", 0xEA),
                    Label = C("F5FBFF"),
                    Category = A("E8F8FF", 0xCE),
                    SnapshotAccent = C("7A5CFF"),
                    SettingsAccent = C("00A6FF"),
                },
                ToolbarTheme.SunrisePaper => new RadialThemePalette
                {
                    TileColors = new[] { C("FF4D00"), C("FFB000"), C("FF2D7A"), C("F24A72"), C("FF7A00"), C("C9184A"), C("FFD166"), C("F72585") },
                    SurfaceInner = A("261008", 0xDE),
                    SurfaceMiddle = A("1A0907", 0xD6),
                    SurfaceOuter = A("100304", 0xC8),
                    Stroke = A("FFD166", 0x72),
                    CoreAccent = C("FFB000"),
                    LabelPlate = A("150505", 0xEA),
                    Label = C("FFF8EC"),
                    Category = A("FFF1D5", 0xCE),
                    SnapshotAccent = C("FFD166"),
                    SettingsAccent = C("FF2D7A"),
                },
                ToolbarTheme.ModernSaaS => new RadialThemePalette
                {
                    TileColors = new[] { C("E63946"), C("00B4D8"), C("2EC4B6"), C("4361EE"), C("FF006E"), C("3A86FF"), C("A8DADC"), C("FFBE0B") },
                    SurfaceInner = A("06152C", 0xDE),
                    SurfaceMiddle = A("07101F", 0xD6),
                    SurfaceOuter = A("030711", 0xC8),
                    Stroke = A("A8DADC", 0x70),
                    CoreAccent = C("2EC4B6"),
                    LabelPlate = A("030817", 0xEA),
                    Label = C("F1FAEE"),
                    Category = A("E6FFFF", 0xCE),
                    SnapshotAccent = C("FFBE0B"),
                    SettingsAccent = C("E63946"),
                },
                ToolbarTheme.FintechInnovator => new RadialThemePalette
                {
                    TileColors = new[] { C("00C853"), C("D6FF00"), C("00A896"), C("F4A261"), C("2A9D8F"), C("FF7A00"), C("39FF14"), C("E9C46A") },
                    SurfaceInner = A("071C17", 0xDE),
                    SurfaceMiddle = A("051410", 0xD6),
                    SurfaceOuter = A("020806", 0xC8),
                    Stroke = A("D6FF00", 0x72),
                    CoreAccent = C("D6FF00"),
                    LabelPlate = A("020A07", 0xEA),
                    Label = C("F4FFE8"),
                    Category = A("E8FFD2", 0xCE),
                    SnapshotAccent = C("E9C46A"),
                    SettingsAccent = C("FF7A00"),
                },
                ToolbarTheme.B2BSolutions => new RadialThemePalette
                {
                    TileColors = new[] { C("2563EB"), C("38BDF8"), C("0F766E"), C("F97316"), C("64748B"), C("14B8A6"), C("3B82F6"), C("93C5FD") },
                    SurfaceInner = A("091423", 0xDE),
                    SurfaceMiddle = A("07101A", 0xD6),
                    SurfaceOuter = A("030712", 0xC8),
                    Stroke = A("93C5FD", 0x70),
                    CoreAccent = C("38BDF8"),
                    LabelPlate = A("030817", 0xEA),
                    Label = C("F8FBFF"),
                    Category = A("DCEBFF", 0xCE),
                    SnapshotAccent = C("F97316"),
                    SettingsAccent = C("2563EB"),
                },
                ToolbarTheme.SeriousTech => new RadialThemePalette
                {
                    TileColors = new[] { C("007BFF"), C("00D1FF"), C("2B2D42"), C("8D99AE"), C("3A86FF"), C("00FF85"), C("FF3D71"), C("ADB5BD") },
                    SurfaceInner = A("0B0F16", 0xE2),
                    SurfaceMiddle = A("070A0F", 0xDA),
                    SurfaceOuter = A("020305", 0xCC),
                    Stroke = A("ADB5BD", 0x74),
                    CoreAccent = C("007BFF"),
                    LabelPlate = A("020305", 0xEC),
                    Label = C("F3F6FA"),
                    Category = A("DDE4EC", 0xCE),
                    SnapshotAccent = C("00FF85"),
                    SettingsAccent = C("007BFF"),
                },
                ToolbarTheme.LegalInsurance => new RadialThemePalette
                {
                    TileColors = new[] { C("8A4F5A"), C("C5A56F"), C("4C1A22"), C("D4AF37"), C("7F1D1D"), C("A855F7"), C("B4B8C5"), C("F59E0B") },
                    SurfaceInner = A("1D0B10", 0xDE),
                    SurfaceMiddle = A("14070A", 0xD6),
                    SurfaceOuter = A("0A0305", 0xC8),
                    Stroke = A("C5A56F", 0x70),
                    CoreAccent = C("C5A56F"),
                    LabelPlate = A("0D0305", 0xEA),
                    Label = C("FFF7E8"),
                    Category = A("FFEAC2", 0xCE),
                    SnapshotAccent = C("D4AF37"),
                    SettingsAccent = C("8A4F5A"),
                },
                ToolbarTheme.DigitalProduct => new RadialThemePalette
                {
                    TileColors = new[] { C("04D9FF"), C("FF00E5"), C("05F4B7"), C("7C3AED"), C("02F5E1"), C("FF2E63"), C("39FF14"), C("3A86FF") },
                    SurfaceInner = A("07072A", 0xE0),
                    SurfaceMiddle = A("05051A", 0xD8),
                    SurfaceOuter = A("010109", 0xCC),
                    Stroke = A("04D9FF", 0x78),
                    CoreAccent = C("05F4B7"),
                    LabelPlate = A("01010B", 0xEA),
                    Label = C("F8FFFF"),
                    Category = A("D9FFFF", 0xD4),
                    SnapshotAccent = C("39FF14"),
                    SettingsAccent = C("FF00E5"),
                },
                ToolbarTheme.JewelTone => new RadialThemePalette
                {
                    TileColors = new[] { C("0A9396"), C("005F73"), C("EE9B00"), C("AE2012"), C("9B5DE5"), C("00F5D4"), C("E9D8A6"), C("CA6702") },
                    SurfaceInner = A("001B22", 0xE0),
                    SurfaceMiddle = A("001219", 0xD8),
                    SurfaceOuter = A("00080C", 0xCC),
                    Stroke = A("94D2BD", 0x72),
                    CoreAccent = C("EE9B00"),
                    LabelPlate = A("00080C", 0xEA),
                    Label = C("FFF6D7"),
                    Category = A("E9D8A6", 0xD4),
                    SnapshotAccent = C("EE9B00"),
                    SettingsAccent = C("AE2012"),
                },
                ToolbarTheme.MinimalCloudMonochrome => new RadialThemePalette
                {
                    TileColors = new[] { C("111111"), C("F8F8F8"), C("707070"), C("D6FF00"), C("2D2D2D"), C("B7B7B7"), C("000000"), C("E5E5E5") },
                    SurfaceInner = A("F7F7F7", 0xE8),
                    SurfaceMiddle = A("D9D9D9", 0xDC),
                    SurfaceOuter = A("FAFAFA", 0xD0),
                    Stroke = A("111111", 0x70),
                    CoreAccent = C("D6FF00"),
                    LabelPlate = A("050505", 0xEA),
                    Label = C("FFFFFF"),
                    Category = A("FFFFFF", 0xD0),
                    SnapshotAccent = C("D6FF00"),
                    SettingsAccent = C("111111"),
                },
                _ => new RadialThemePalette
                {
                    TileColors = new[] { C("205BFF"), C("D616FF"), C("00C28C"), C("FF4D00"), C("7035FF"), C("008CFF"), C("E8125B"), C("12B82F") },
                    SurfaceInner = A("080A12", 0xDE),
                    SurfaceMiddle = A("0D101E", 0xD4),
                    SurfaceOuter = A("030408", 0xC8),
                    Stroke = A("FFFFFF", 0x66),
                    CoreAccent = C("D616FF"),
                    LabelPlate = A("04060D", 0xE8),
                    Label = C("FFFFFF"),
                    Category = A("FFFFFF", 0xCC),
                    SnapshotAccent = C("FFB000"),
                    SettingsAccent = C("FF3B5C"),
                },
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
                            if (IsScreenshotAction(entry.Item.Button))
                            {
                                await LaunchScreenshotCaptureAsync().ConfigureAwait(true);
                            }
                            else
                            {
                                await _actionExecutor.ExecuteAsync(entry.Item.Group, entry.Item.Button, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        break;
                    case RadialEntryKind.Snapshot:
                        await HandleQuickSnapshotAsync(null, SnapshotFlightOrigin.Ring).ConfigureAwait(true);
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
