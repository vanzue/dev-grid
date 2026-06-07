// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services.Display;
using TopToolbar.Services.HotCorners;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    internal sealed class CornerOverlayWindow : WindowEx, IDisposable
    {
        private const double SizeDip = 190.0;
        private const double GlowRadiusDip = SizeDip * 1.05;
        private const double ArcRadiusDip = SizeDip * 0.72;
        private const double ArcThicknessDip = 6.0;
        private const double HintLabelWidthDip = 96.0;
        private const double HintLabelInsetDip = 32.0;
        private const double HintLabelBottomOffsetDip = 50.0;
        private const double HintLabelTopOffsetDip = 46.0;

        private static readonly IntPtr HwndTopMost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpFrameChanged = 0x0020;

        private readonly Canvas _root;
        private readonly Ellipse _glow;
        private readonly Path _track;
        private readonly Path _progress;
        private readonly TextBlock _hintLabel;

        private Color _accent = Color.FromArgb(0xFF, 0xD1, 0x34, 0x38);
        private Color _label = Color.FromArgb(0xFF, 0x2F, 0x3A, 0x3F);

        private bool _shown;
        private bool _stylesApplied;
        private IntPtr _oldWndProc;
        private WndProcDelegate _newWndProc;
        private IntPtr _hwnd;
        private bool _disposed;
        private double _surfaceWidthDip = SizeDip;
        private double _surfaceHeightDip = SizeDip;
        private double _cornerCenterXDip;
        private double _cornerCenterYDip;
        private double _metricScale = 1.0;
        private DateTime _lastPlacementLogUtc = DateTime.MinValue;
        private string _lastPlacementLogSignature = string.Empty;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public CornerOverlayWindow()
        {
            Title = string.Empty;
            IsTitleBarVisible = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            _glow = new Ellipse
            {
                Width = GlowRadiusDip * 2,
                Height = GlowRadiusDip * 2,
                IsHitTestVisible = false,
            };

            _track = new Path
            {
                StrokeThickness = ArcThicknessDip,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                Opacity = 0.0,
            };

            _progress = new Path
            {
                StrokeThickness = ArcThicknessDip,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };

            _hintLabel = new TextBlock
            {
                Width = HintLabelWidthDip,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                Opacity = 0.0,
            };

            _root = new Canvas
            {
                Width = SizeDip,
                Height = SizeDip,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false,
            };
            _root.Children.Add(_glow);
            _root.Children.Add(_track);
            _root.Children.Add(_progress);
            _root.Children.Add(_hintLabel);

            Content = _root;

            ConfigureChrome();
            Activate();
            ConfigureChrome();
            ApplyFramelessStyles();
            HookPassthrough();
            AppWindow.Hide();
        }

        public void ApplyTheme(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            _accent = ResolveColor(resources, "ToolbarNotificationAccentBrush", _accent);
            _label = ResolveColor(resources, "ToolbarLabelBrush", _label);
            RefreshBrushes();
        }

        public void Update(HotCornerHoverState state)
        {
            if (_disposed)
            {
                return;
            }

            if (!state.Active)
            {
                Hide();
                return;
            }

            ShowAt(state.Corner, state.MonitorBounds, state.Scale, state.Progress);
        }

        public void ShowHint(HotCorner corner, DisplayRect bounds, double scale, string label)
        {
            if (_disposed)
            {
                return;
            }

            if (scale <= 0)
            {
                scale = 1.0;
            }

            var sizePx = (int)Math.Round(SizeDip * scale);
            if (sizePx <= 0)
            {
                return;
            }

            try
            {
                ResolvePlacement(corner, bounds, sizePx, out var x, out var y);
                AppWindow.MoveAndResize(new RectInt32(x, y, sizePx, sizePx));
                if (!_shown)
                {
                    _root.Opacity = 0.0;
                    AppWindow.Show(false);
                    _shown = true;
                }

                ConfigureSurfaceFromActualWindow("hint", corner, bounds, scale, x, y, sizePx);

                _root.Opacity = 1.0;
                ResetRootTransform();
                BuildGeometry(corner);
                UpdateProgress(corner, 0.0);
                _hintLabel.Text = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
                _hintLabel.Opacity = string.IsNullOrWhiteSpace(_hintLabel.Text) ? 0.0 : 0.46;
                PositionHintLabel(corner);
                MakeTopMost();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: hint show failed - {ex.Message}");
            }
        }

        private void ShowAt(HotCorner corner, DisplayRect bounds, double scale, double progress)
        {
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var sizePx = (int)Math.Round(SizeDip * scale);
            if (sizePx <= 0)
            {
                return;
            }

            try
            {
                ResolvePlacement(corner, bounds, sizePx, out var x, out var y);
                AppWindow.MoveAndResize(new RectInt32(x, y, sizePx, sizePx));
                var wasHidden = !_shown;
                if (!_shown)
                {
                    _root.Opacity = 0.0;
                    AppWindow.Show(false);
                    _shown = true;
                }

                ConfigureSurfaceFromActualWindow("hover", corner, bounds, scale, x, y, sizePx);

                BuildGeometry(corner);
                UpdateProgress(corner, progress);

                if (wasHidden)
                {
                    PlayEntranceFade();
                }

                MakeTopMost();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: show failed - {ex.Message}");
            }
        }

        private void PlayEntranceFade()
        {
            try
            {
                var fade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };

                ResetRootTransform();

                var storyboard = new Storyboard();

                Storyboard.SetTarget(fade, _root);
                Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Children.Add(fade);

                storyboard.Begin();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: entrance fade failed - {ex.Message}");
            }
        }

        private void ResetRootTransform()
        {
            _root.RenderTransform = null;
        }

        public void Hide()
        {
            if (!_shown)
            {
                return;
            }

            _shown = false;
            try
            {
                AppWindow.Hide();
            }
            catch
            {
            }
        }

        private static void ResolvePlacement(HotCorner corner, DisplayRect bounds, int sizePx, out int x, out int y)
        {
            switch (corner)
            {
                case HotCorner.TopLeft:
                    x = bounds.Left;
                    y = bounds.Top;
                    break;
                case HotCorner.TopRight:
                    x = bounds.Right - sizePx;
                    y = bounds.Top;
                    break;
                case HotCorner.BottomLeft:
                    x = bounds.Left;
                    y = bounds.Bottom - sizePx;
                    break;
                default:
                    x = bounds.Right - sizePx;
                    y = bounds.Bottom - sizePx;
                    break;
            }
        }

        private void ConfigureSurfaceFromActualWindow(
            string mode,
            HotCorner corner,
            DisplayRect bounds,
            double targetScale,
            int requestedX,
            int requestedY,
            int requestedSizePx)
        {
            var xamlScale = ResolveXamlScale(targetScale);
            var actualPosition = AppWindow?.Position ?? new PointInt32(0, 0);
            var actualSize = AppWindow?.Size ?? new SizeInt32(requestedSizePx, requestedSizePx);

            var widthPx = actualSize.Width > 0 ? actualSize.Width : requestedSizePx;
            var heightPx = actualSize.Height > 0 ? actualSize.Height : requestedSizePx;

            _surfaceWidthDip = Math.Max(1.0, widthPx / xamlScale);
            _surfaceHeightDip = Math.Max(1.0, heightPx / xamlScale);
            _metricScale = Math.Max(0.1, targetScale / xamlScale);

            _root.Width = _surfaceWidthDip;
            _root.Height = _surfaceHeightDip;

            var targetX = corner == HotCorner.TopRight || corner == HotCorner.BottomRight
                ? bounds.Right
                : bounds.Left;
            var targetY = corner == HotCorner.BottomLeft || corner == HotCorner.BottomRight
                ? bounds.Bottom
                : bounds.Top;

            _cornerCenterXDip = Math.Clamp((targetX - actualPosition.X) / xamlScale, 0.0, _surfaceWidthDip);
            _cornerCenterYDip = Math.Clamp((targetY - actualPosition.Y) / xamlScale, 0.0, _surfaceHeightDip);

            LogPlacement(
                mode,
                corner,
                bounds,
                targetScale,
                xamlScale,
                requestedX,
                requestedY,
                requestedSizePx,
                actualPosition,
                actualSize,
                targetX,
                targetY);
        }

        private double ResolveXamlScale(double fallbackScale)
        {
            var scale = _root.XamlRoot?.RasterizationScale ?? fallbackScale;
            if (scale <= 0)
            {
                scale = fallbackScale;
            }

            return scale > 0 ? scale : 1.0;
        }

        private double Scaled(double value)
        {
            return value * _metricScale;
        }

        private void LogPlacement(
            string mode,
            HotCorner corner,
            DisplayRect bounds,
            double targetScale,
            double xamlScale,
            int requestedX,
            int requestedY,
            int requestedSizePx,
            PointInt32 actualPosition,
            SizeInt32 actualSize,
            int targetX,
            int targetY)
        {
            var signature =
                $"{mode}|{corner}|req={requestedX},{requestedY},{requestedSizePx}|actual={actualPosition.X},{actualPosition.Y},{actualSize.Width},{actualSize.Height}|center={_cornerCenterXDip:F1},{_cornerCenterYDip:F1}|scale={targetScale:F3},{xamlScale:F3}";
            var now = DateTime.UtcNow;
            if (string.Equals(signature, _lastPlacementLogSignature, StringComparison.Ordinal) &&
                (now - _lastPlacementLogUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            _lastPlacementLogSignature = signature;
            _lastPlacementLogUtc = now;

            AppLogger.LogInfo(
                $"CornerOverlayPlacement: mode={mode}, corner={corner}, monitor=({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}) rightBottom=({bounds.Right},{bounds.Bottom}), targetScreen=({targetX},{targetY}), requestedWindow=({requestedX},{requestedY},{requestedSizePx},{requestedSizePx}), actualWindow=({actualPosition.X},{actualPosition.Y},{actualSize.Width},{actualSize.Height}), targetScale={targetScale:F3}, xamlScale={xamlScale:F3}, metricScale={_metricScale:F3}, surfaceDip=({_surfaceWidthDip:F1},{_surfaceHeightDip:F1}), cornerCenterDip=({_cornerCenterXDip:F1},{_cornerCenterYDip:F1}), rootDip=({_root.Width:F1},{_root.Height:F1})");
        }

        private (double cx, double cy, double startDeg) CornerGeometry(HotCorner corner)
        {
            return corner switch
            {
                HotCorner.TopLeft => (_cornerCenterXDip, _cornerCenterYDip, 0.0),
                HotCorner.TopRight => (_cornerCenterXDip, _cornerCenterYDip, 90.0),
                HotCorner.BottomLeft => (_cornerCenterXDip, _cornerCenterYDip, 270.0),
                _ => (_cornerCenterXDip, _cornerCenterYDip, 180.0),
            };
        }

        private void BuildGeometry(HotCorner corner)
        {
            var (cx, cy, _) = CornerGeometry(corner);

            var glowRadius = Scaled(GlowRadiusDip);
            _glow.Width = glowRadius * 2.0;
            _glow.Height = glowRadius * 2.0;
            Canvas.SetLeft(_glow, cx - glowRadius);
            Canvas.SetTop(_glow, cy - glowRadius);

            // Full quarter track arc (faint).
            _track.StrokeThickness = Scaled(ArcThicknessDip);
            _progress.StrokeThickness = Scaled(ArcThicknessDip);
            _track.Data = BuildArc(corner, 1.0, Scaled(ArcRadiusDip));
        }

        private void UpdateProgress(HotCorner corner, double progress)
        {
            progress = Math.Clamp(progress, 0.0, 1.0);

            // Glow grows and brightens with progress for a responsive feel.
            var eased = 0.35 + (0.65 * progress);
            _glow.Opacity = eased;
            var scale = 0.7 + (0.3 * progress);
            _glow.RenderTransformOrigin = new Point(0.5, 0.5);
            _glow.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };

            _track.Opacity = 0.18;
            _progress.Opacity = 1.0;
            _progress.Data = progress <= 0.001 ? null : BuildArc(corner, progress, Scaled(ArcRadiusDip));
            _hintLabel.Opacity = 0.0;
        }

        private Geometry BuildArc(HotCorner corner, double fraction, double radiusDip)
        {
            fraction = Math.Clamp(fraction, 0.0, 1.0);
            var (cx, cy, startDeg) = CornerGeometry(corner);
            var sweep = fraction * 90.0;

            var startRad = startDeg * Math.PI / 180.0;
            var endRad = (startDeg + sweep) * Math.PI / 180.0;

            var start = new Point(
                cx + (radiusDip * Math.Cos(startRad)),
                cy + (radiusDip * Math.Sin(startRad)));
            var end = new Point(
                cx + (radiusDip * Math.Cos(endRad)),
                cy + (radiusDip * Math.Sin(endRad)));

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radiusDip, radiusDip),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false,
                RotationAngle = 0,
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private void PositionHintLabel(HotCorner corner)
        {
            _hintLabel.TextAlignment =
                corner == HotCorner.TopRight || corner == HotCorner.BottomRight
                    ? TextAlignment.Right
                    : TextAlignment.Left;

            var labelWidth = Scaled(HintLabelWidthDip);
            var labelInset = Scaled(HintLabelInsetDip);
            _hintLabel.Width = labelWidth;

            var left = corner == HotCorner.TopRight || corner == HotCorner.BottomRight
                ? _cornerCenterXDip - labelInset - labelWidth
                : _cornerCenterXDip + labelInset;

            var top = corner == HotCorner.BottomLeft || corner == HotCorner.BottomRight
                ? _cornerCenterYDip - Scaled(HintLabelBottomOffsetDip)
                : _cornerCenterYDip + Scaled(HintLabelTopOffsetDip);

            Canvas.SetLeft(_hintLabel, Math.Clamp(left, 0.0, Math.Max(0.0, _surfaceWidthDip - labelWidth)));
            Canvas.SetTop(_hintLabel, Math.Clamp(top, 0.0, Math.Max(0.0, _surfaceHeightDip - Scaled(HintLabelBottomOffsetDip))));
        }

        private void RefreshBrushes()
        {
            var glowBrush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accent, 0xCC), Offset = 0.0 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accent, 0x55), Offset = 0.45 });
            glowBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_accent, 0x00), Offset = 1.0 });
            _glow.Fill = glowBrush;

            _track.Stroke = new SolidColorBrush(WithAlpha(_label, 0xFF));
            _progress.Stroke = new SolidColorBrush(WithAlpha(_accent, 0xFF));
            _hintLabel.Foreground = new SolidColorBrush(WithAlpha(_label, 0xCC));
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Color ResolveColor(ResourceDictionary resources, string key, Color fallback)
        {
            if (resources.TryGetValue(key, out var value))
            {
                if (value is SolidColorBrush solid)
                {
                    return solid.Color;
                }

                if (value is GradientBrush gradient && gradient.GradientStops != null && gradient.GradientStops.Count > 0)
                {
                    return gradient.GradientStops[0].Color;
                }
            }

            return fallback;
        }

        private void ConfigureChrome()
        {
            try
            {
                if (AppWindow == null)
                {
                    return;
                }

                AppWindow.IsShownInSwitchers = false;
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: chrome config failed - {ex.Message}");
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
                const int GwlStyle = -16;
                const int GwlExStyle = -20;
                const int WsCaption = 0x00C00000;
                const int WsThickFrame = 0x00040000;
                const int WsMinimizeBox = 0x00020000;
                const int WsMaximizeBox = 0x00010000;
                const int WsSysMenu = 0x00080000;
                const int WsPopup = unchecked((int)0x80000000);
                const int WsVisible = 0x10000000;
                const int WsExToolWindow = 0x00000080;
                const int WsExTopmost = 0x00000008;
                const int WsExNoActivate = 0x08000000;
                const int WsExTransparent = 0x00000020;
                const int WsExLayered = 0x00080000;

                var style = GetWindowLong(hwnd, GwlStyle);
                style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
                style |= WsPopup | WsVisible;
                _ = SetWindowLong(hwnd, GwlStyle, style);

                var exStyle = GetWindowLong(hwnd, GwlExStyle);
                exStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate | WsExTransparent | WsExLayered;
                _ = SetWindowLong(hwnd, GwlExStyle, exStyle);

                _ = SetWindowPos(
                    hwnd,
                    HwndTopMost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow | SwpFrameChanged);

                const int DwmwaBorderColor = 34;
                uint dwmColorNone = 0xFFFFFFFE;
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref dwmColorNone, sizeof(uint));

                _stylesApplied = true;
                EnsureClickThroughStyles(hwnd);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: frameless styles failed - {ex.Message}");
            }
        }

        private void HookPassthrough()
        {
            if (_oldWndProc != IntPtr.Zero)
            {
                return;
            }

            try
            {
                _hwnd = this.GetWindowHandle();
                if (_hwnd == IntPtr.Zero)
                {
                    return;
                }

                _newWndProc = OverlayWndProc;
                _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_newWndProc));
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: hook wndproc failed - {ex.Message}");
            }
        }

        private void UnhookPassthrough()
        {
            if (_hwnd == IntPtr.Zero || _oldWndProc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _ = SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);
            }
            catch
            {
            }
            finally
            {
                _oldWndProc = IntPtr.Zero;
                _newWndProc = null;
            }
        }

        private IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmNcHitTest)
            {
                return new IntPtr(HtTransparent);
            }

            if (msg == WmMouseActivate)
            {
                return new IntPtr(MaNoActivate);
            }

            return _oldWndProc != IntPtr.Zero
                ? CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;
        }

        private void MakeTopMost()
        {
            var hwnd = this.GetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            EnsureClickThroughStyles(hwnd);
            _ = SetWindowPos(
                hwnd,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        private void EnsureClickThroughStyles(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                const int GwlExStyle = -20;
                const int WsExToolWindow = 0x00000080;
                const int WsExTopmost = 0x00000008;
                const int WsExNoActivate = 0x08000000;
                const int WsExTransparent = 0x00000020;
                const int WsExLayered = 0x00080000;

                var exStyle = GetWindowLong(hwnd, GwlExStyle);
                var desired = exStyle | WsExToolWindow | WsExTopmost | WsExNoActivate | WsExTransparent | WsExLayered;
                if (desired != exStyle)
                {
                    _ = SetWindowLong(hwnd, GwlExStyle, desired);
                    _ = SetWindowPos(
                        hwnd,
                        HwndTopMost,
                        0,
                        0,
                        0,
                        0,
                        SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"CornerOverlay: click-through style restore failed - {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnhookPassthrough();
            try
            {
                Close();
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

        private const int GwlWndProc = -4;
        private const uint WmNcHitTest = 0x0084;
        private const uint WmMouseActivate = 0x0021;
        private const int HtTransparent = -1;
        private const int MaNoActivate = 3;
    }
}
