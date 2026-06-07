// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Logging;
using TopToolbar.Services.HotCorners;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    /// <summary>
    /// Topmost, click-through overlay that plays a "camera" animation: a shutter flash followed by the
    /// captured screenshot shrinking and flying into the freshly created workspace button.
    /// </summary>
    internal sealed class PhotoFlightWindow : WindowEx, IDisposable
    {
        private static readonly IntPtr HwndTopMost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpFrameChanged = 0x0020;

        private readonly Canvas _root;
        private readonly Image _photo;
        private readonly Border _photoHost;
        private readonly Rectangle _flash;

        private bool _stylesApplied;
        private bool _disposed;
        private bool _busy;

        public PhotoFlightWindow()
        {
            Title = string.Empty;
            IsTitleBarVisible = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            _photo = new Image
            {
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };

            _photoHost = new Border
            {
                Child = _photo,
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            };

            _flash = new Rectangle
            {
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = 0.0,
                IsHitTestVisible = false,
            };

            _root = new Canvas
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false,
            };
            _root.Children.Add(_photoHost);
            _root.Children.Add(_flash);

            Content = _root;

            ConfigureChrome();
            Activate();
            ConfigureChrome();
            ApplyFramelessStyles();
            AppWindow.Hide();
        }

        public async Task PlayAsync(
            CapturedBitmap capture,
            RectInt32 monitorPx,
            RectInt32 targetButtonPx,
            double scale)
        {
            if (_disposed || _busy || !capture.IsValid)
            {
                return;
            }

            if (scale <= 0)
            {
                scale = 1.0;
            }

            _busy = true;
            try
            {
                var source = BuildBitmap(capture);
                if (source == null)
                {
                    return;
                }

                _photo.Source = source;

                var monW = Math.Max(1, monitorPx.Width);
                var monH = Math.Max(1, monitorPx.Height);
                var widthDip = monW / scale;
                var heightDip = monH / scale;

                _root.Width = widthDip;
                _root.Height = heightDip;

                _photoHost.Width = widthDip;
                _photoHost.Height = heightDip;
                Canvas.SetLeft(_photoHost, 0);
                Canvas.SetTop(_photoHost, 0);

                _flash.Width = widthDip;
                _flash.Height = heightDip;
                _flash.Opacity = 0.0;
                Canvas.SetLeft(_flash, 0);
                Canvas.SetTop(_flash, 0);

                // Target in monitor-local physical pixels -> scale factors and offset (DIPs).
                var localX = targetButtonPx.X - monitorPx.X;
                var localY = targetButtonPx.Y - monitorPx.Y;
                var targetScaleX = (float)Math.Clamp(targetButtonPx.Width / (double)monW, 0.02, 1.0);
                var targetScaleY = (float)Math.Clamp(targetButtonPx.Height / (double)monH, 0.02, 1.0);
                var targetScale = Math.Min(targetScaleX, targetScaleY);
                var offsetX = (float)(localX / scale);
                var offsetY = (float)(localY / scale);

                // Center the (uniformly scaled) photo onto the button rect.
                var scaledWidthDip = widthDip * targetScale;
                var scaledHeightDip = heightDip * targetScale;
                var btnWidthDip = targetButtonPx.Width / scale;
                var btnHeightDip = targetButtonPx.Height / scale;
                offsetX += (float)((btnWidthDip - scaledWidthDip) / 2.0);
                offsetY += (float)((btnHeightDip - scaledHeightDip) / 2.0);

                try
                {
                    AppWindow.MoveAndResize(monitorPx);
                    AppWindow.Show(false);
                    MakeTopMost();
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"PhotoFlight: show failed - {ex.Message}");
                    return;
                }

                await PlayShutterAsync().ConfigureAwait(true);
                await PlayFlightAsync(targetScale, offsetX, offsetY).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"PhotoFlight: play failed - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                try
                {
                    AppWindow.Hide();
                }
                catch
                {
                }

                _photo.Source = null;
                _busy = false;
            }
        }

        private async Task PlayShutterAsync()
        {
            var visual = ElementCompositionPreview.GetElementVisual(_flash);
            var compositor = visual.Compositor;

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0.0f, 0.0f);
            anim.InsertKeyFrame(0.35f, 0.85f);
            anim.InsertKeyFrame(1.0f, 0.0f);
            anim.Duration = TimeSpan.FromMilliseconds(230);

            _flash.Opacity = 1.0; // let the animation drive the visible opacity
            await RunBatchAsync(compositor, () => visual.StartAnimation("Opacity", anim)).ConfigureAwait(true);
            _flash.Opacity = 0.0;
        }

        private async Task PlayFlightAsync(float targetScale, float offsetX, float offsetY)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_photoHost);
            var compositor = visual.Compositor;

            visual.CenterPoint = new Vector3(0f, 0f, 0f);
            visual.Scale = new Vector3(1f, 1f, 1f);
            visual.Offset = new Vector3(0f, 0f, 0f);
            visual.Opacity = 1.0f;

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.5f, 0.0f),
                new Vector2(0.2f, 1.0f));

            var duration = TimeSpan.FromMilliseconds(620);

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1.0f, new Vector3(targetScale, targetScale, 1f), ease);
            scaleAnim.Duration = duration;

            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(1.0f, new Vector3(offsetX, offsetY, 0f), ease);
            offsetAnim.Duration = duration;

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0.0f, 1.0f);
            opacityAnim.InsertKeyFrame(0.72f, 1.0f);
            opacityAnim.InsertKeyFrame(1.0f, 0.0f);
            opacityAnim.Duration = duration;

            await RunBatchAsync(compositor, () =>
            {
                visual.StartAnimation("Scale", scaleAnim);
                visual.StartAnimation("Offset", offsetAnim);
                visual.StartAnimation("Opacity", opacityAnim);
            }).ConfigureAwait(true);
        }

        private static Task RunBatchAsync(Compositor compositor, Action start)
        {
            var tcs = new TaskCompletionSource<bool>();
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
            {
                batch.Completed -= OnCompleted;
                tcs.TrySetResult(true);
            }

            batch.Completed += OnCompleted;
            start();
            batch.End();
            return tcs.Task;
        }

        private static WriteableBitmap BuildBitmap(CapturedBitmap capture)
        {
            try
            {
                var wb = new WriteableBitmap(capture.Width, capture.Height);
                using (var stream = wb.PixelBuffer.AsStream())
                {
                    stream.Write(capture.Bgra, 0, capture.Bgra.Length);
                }

                return wb;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"PhotoFlight: bitmap build failed - {ex.Message}");
                return null;
            }
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
                AppLogger.LogWarning($"PhotoFlight: chrome config failed - {ex.Message}");
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

                var style = GetWindowLong(hwnd, GwlStyle);
                style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
                style |= WsPopup | WsVisible;
                _ = SetWindowLong(hwnd, GwlStyle, style);

                var exStyle = GetWindowLong(hwnd, GwlExStyle);
                exStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate | WsExTransparent;
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
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"PhotoFlight: frameless styles failed - {ex.Message}");
            }
        }

        private void MakeTopMost()
        {
            var hwnd = this.GetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _ = SetWindowPos(
                hwnd,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);
    }
}
