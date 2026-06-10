// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TopToolbar.Logging;
using TopToolbar.Services.HotCorners;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    /// <summary>
    /// Full-monitor, top-most overlay that freezes the screen and lets the user draw, move and resize
    /// a rectangular selection, then copy it to the clipboard. Interaction contract:
    /// <list type="bullet">
    /// <item>Idle (frozen, nothing drawn): left-drag starts drawing a rectangle; Esc cancels and closes.</item>
    /// <item>Drawing: releasing the left button finalizes the selection.</item>
    /// <item>Selected: drag inside to move, drag a handle to resize; Esc clears the selection (redraw);
    /// Ctrl+C (or Enter) copies the cropped region to the clipboard and closes.</item>
    /// </list>
    /// </summary>
    internal sealed class ScreenshotOverlayWindow : WindowEx, IDisposable
    {
        private const double MinSelection = 3.0;
        private const double HandleSize = 10.0;
        private const double HandleHitPadding = 6.0;

        private enum Mode
        {
            Idle,
            Drawing,
            Moving,
            Resizing,
        }

        private readonly CapturedBitmap _capture;
        private readonly RectInt32 _monitorPx;
        private readonly double _scale;
        private readonly double _widthDip;
        private readonly double _heightDip;

        private readonly CursorGrid _root;
        private readonly Canvas _canvas;
        private readonly Rectangle _maskTop = NewMask();
        private readonly Rectangle _maskBottom = NewMask();
        private readonly Rectangle _maskLeft = NewMask();
        private readonly Rectangle _maskRight = NewMask();
        private readonly Rectangle _selectionBorder;
        private readonly Rectangle[] _handles = new Rectangle[8];
        private readonly TextBlock _hint;

        private Mode _mode = Mode.Idle;
        private bool _hasSelection;
        private double _selX;
        private double _selY;
        private double _selW;
        private double _selH;

        private double _dragStartX;
        private double _dragStartY;
        private double _moveOffsetX;
        private double _moveOffsetY;
        private int _activeHandle = -1;
        private InputSystemCursorShape? _currentCursor;
        private bool _copied;
        private bool _disposed;

        public ScreenshotOverlayWindow(CapturedBitmap capture, RectInt32 monitorPx, double scale)
        {
            _capture = capture;
            _monitorPx = monitorPx;
            _scale = scale <= 0 ? 1.0 : scale;
            _widthDip = _capture.Width / _scale;
            _heightDip = _capture.Height / _scale;

            Title = string.Empty;
            IsTitleBarVisible = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            var frozen = new Image
            {
                Source = BuildBitmap(capture),
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };

            _canvas = new Canvas { IsHitTestVisible = false };
            _canvas.Children.Add(_maskTop);
            _canvas.Children.Add(_maskBottom);
            _canvas.Children.Add(_maskLeft);
            _canvas.Children.Add(_maskRight);

            _selectionBorder = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0xA3, 0xFF)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Colors.Transparent),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _canvas.Children.Add(_selectionBorder);

            for (int i = 0; i < _handles.Length; i++)
            {
                var handle = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = new SolidColorBrush(Colors.White),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0xA3, 0xFF)),
                    StrokeThickness = 1.0,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                };
                _handles[i] = handle;
                _canvas.Children.Add(handle);
            }

            _hint = new TextBlock
            {
                Text = "Drag to select   ·   Esc to redraw / cancel   ·   Ctrl+C to copy",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 13,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 0, 0),
                Opacity = 0.85,
            };

            _root = new CursorGrid
            {
                Background = new SolidColorBrush(Colors.Transparent),
                IsTabStop = true,
            };
            _root.Children.Add(frozen);
            _root.Children.Add(_canvas);
            _root.Children.Add(_hint);

            _root.PointerPressed += OnPointerPressed;
            _root.PointerMoved += OnPointerMoved;
            _root.PointerReleased += OnPointerReleased;
            _root.KeyDown += OnKeyDown;

            // Window-scoped accelerators so Ctrl+C / Esc / Enter work even if per-element KeyDown
            // focus is flaky on a borderless overlay.
            AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, () => TryCopySelection());
            AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, HandleEscapeKey);
            AddAccelerator(VirtualKey.Enter, VirtualKeyModifiers.None, () => TryCopySelection());

            Content = _root;

            ConfigureChrome();
            AppWindow.MoveAndResize(_monitorPx);
            Activate();
            ConfigureChrome();

            UpdateVisuals();
            ApplyCursor(InputSystemCursorShape.Cross);

            _root.SizeChanged += (_, _) => UpdateVisuals();
            _root.Loaded += (_, _) => ForceForegroundAndFocus();
            ForceForegroundAndFocus();

            this.Closed += (_, _) => Dispose();
        }

        // A borderless overlay does not reliably receive keyboard focus from Activate() alone, which
        // means XAML accelerators / KeyDown never fire. Force the window to the foreground and place
        // Win32 + XAML focus on it so Ctrl+C / Esc work.
        private void ForceForegroundAndFocus()
        {
            try
            {
                var hwnd = this.GetWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                    SetFocus(hwnd);
                }

                _ = _root.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Screenshot: focus failed - {ex.Message}");
            }
        }

        private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action handler)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = modifiers,
                ScopeOwner = null,
            };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                handler();
            };
            _root.KeyboardAccelerators.Add(accelerator);
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
                    presenter.SetBorderAndTitleBar(false, false);
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsAlwaysOnTop = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Screenshot: chrome config failed - {ex.Message}");
            }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_root).Position;
            _root.CapturePointer(e.Pointer);
            _ = _root.Focus(FocusState.Programmatic);

            if (_hasSelection)
            {
                int handle = HitTestHandle(p.X, p.Y);
                if (handle >= 0)
                {
                    _mode = Mode.Resizing;
                    _activeHandle = handle;
                    UpdateCursor(p.X, p.Y);
                    return;
                }

                if (IsInsideSelection(p.X, p.Y))
                {
                    _mode = Mode.Moving;
                    _moveOffsetX = p.X - _selX;
                    _moveOffsetY = p.Y - _selY;
                    UpdateCursor(p.X, p.Y);
                    return;
                }
            }

            // Start a fresh rectangle.
            _mode = Mode.Drawing;
            _dragStartX = Clamp(p.X, 0, _widthDip);
            _dragStartY = Clamp(p.Y, 0, _heightDip);
            _selX = _dragStartX;
            _selY = _dragStartY;
            _selW = 0;
            _selH = 0;
            _hasSelection = false;
            UpdateVisuals();
            UpdateCursor(p.X, p.Y);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_root).Position;
            UpdateCursor(p.X, p.Y);

            if (_mode == Mode.Idle)
            {
                return;
            }

            double mx = Clamp(p.X, 0, _widthDip);
            double my = Clamp(p.Y, 0, _heightDip);

            switch (_mode)
            {
                case Mode.Drawing:
                    _selX = Math.Min(_dragStartX, mx);
                    _selY = Math.Min(_dragStartY, my);
                    _selW = Math.Abs(mx - _dragStartX);
                    _selH = Math.Abs(my - _dragStartY);
                    break;

                case Mode.Moving:
                    _selX = Clamp(mx - _moveOffsetX, 0, _widthDip - _selW);
                    _selY = Clamp(my - _moveOffsetY, 0, _heightDip - _selH);
                    break;

                case Mode.Resizing:
                    ApplyResize(mx, my);
                    break;
            }

            UpdateVisuals();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _root.ReleasePointerCapture(e.Pointer);

            if (_mode == Mode.Drawing)
            {
                _hasSelection = _selW >= MinSelection && _selH >= MinSelection;
                if (!_hasSelection)
                {
                    // Treat a tiny drag/click as "no selection yet" (stay in idle, redraw allowed).
                    _selW = 0;
                    _selH = 0;
                }
            }
            else if (_mode == Mode.Moving || _mode == Mode.Resizing)
            {
                _hasSelection = _selW >= MinSelection && _selH >= MinSelection;
            }

            _mode = Mode.Idle;
            _activeHandle = -1;
            UpdateVisuals();
            var rp = e.GetCurrentPoint(_root).Position;
            UpdateCursor(rp.X, rp.Y);
            ForceForegroundAndFocus();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HandleEscapeKey();
                return;
            }

            if (e.Key == VirtualKey.C && IsControlDown())
            {
                e.Handled = true;
                TryCopySelection();
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                TryCopySelection();
            }
        }

        private void HandleEscapeKey()
        {
            if (_hasSelection)
            {
                // Clear the selection and return to the draw state.
                _hasSelection = false;
                _selW = 0;
                _selH = 0;
                _mode = Mode.Idle;
                UpdateVisuals();
            }
            else
            {
                // Nothing drawn yet: cancel the whole capture.
                CloseOverlay();
            }
        }

        private void TryCopySelection()
        {
            if (_hasSelection)
            {
                _ = CopyAndCloseAsync();
            }
        }

        private static bool IsControlDown()
        {
            const int VkControl = 0x11;
            return (GetKeyState(VkControl) & 0x8000) != 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private void ApplyResize(double mx, double my)
        {
            double left = _selX;
            double top = _selY;
            double right = _selX + _selW;
            double bottom = _selY + _selH;

            // Handles: 0=TL 1=T 2=TR 3=R 4=BR 5=B 6=BL 7=L
            bool touchLeft = _activeHandle is 0 or 6 or 7;
            bool touchRight = _activeHandle is 2 or 3 or 4;
            bool touchTop = _activeHandle is 0 or 1 or 2;
            bool touchBottom = _activeHandle is 4 or 5 or 6;

            if (touchLeft)
            {
                left = mx;
            }

            if (touchRight)
            {
                right = mx;
            }

            if (touchTop)
            {
                top = my;
            }

            if (touchBottom)
            {
                bottom = my;
            }

            _selX = Math.Min(left, right);
            _selY = Math.Min(top, bottom);
            _selW = Math.Abs(right - left);
            _selH = Math.Abs(bottom - top);
        }

        private int HitTestHandle(double x, double y)
        {
            if (!_hasSelection)
            {
                return -1;
            }

            var centers = HandleCenters();
            double pad = (HandleSize / 2.0) + HandleHitPadding;
            for (int i = 0; i < centers.Length; i++)
            {
                if (Math.Abs(x - centers[i].X) <= pad && Math.Abs(y - centers[i].Y) <= pad)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsInsideSelection(double x, double y)
        {
            return x >= _selX && x <= _selX + _selW && y >= _selY && y <= _selY + _selH;
        }

        private (double X, double Y)[] HandleCenters()
        {
            double l = _selX;
            double t = _selY;
            double r = _selX + _selW;
            double b = _selY + _selH;
            double cx = _selX + (_selW / 2.0);
            double cy = _selY + (_selH / 2.0);

            return new[]
            {
                (l, t), (cx, t), (r, t),
                (r, cy),
                (r, b), (cx, b), (l, b),
                (l, cy),
            };
        }

        private void UpdateCursor(double x, double y)
        {
            InputSystemCursorShape shape;
            switch (_mode)
            {
                case Mode.Moving:
                    shape = InputSystemCursorShape.SizeAll;
                    break;
                case Mode.Resizing:
                    shape = HandleCursor(_activeHandle);
                    break;
                case Mode.Drawing:
                    shape = InputSystemCursorShape.Cross;
                    break;
                default:
                    if (_hasSelection)
                    {
                        int handle = HitTestHandle(x, y);
                        if (handle >= 0)
                        {
                            shape = HandleCursor(handle);
                        }
                        else if (IsInsideSelection(x, y))
                        {
                            shape = InputSystemCursorShape.SizeAll;
                        }
                        else
                        {
                            shape = InputSystemCursorShape.Cross;
                        }
                    }
                    else
                    {
                        shape = InputSystemCursorShape.Cross;
                    }

                    break;
            }

            ApplyCursor(shape);
        }

        private void ApplyCursor(InputSystemCursorShape shape)
        {
            if (_currentCursor == shape)
            {
                return;
            }

            _currentCursor = shape;
            _root.SetCursorShape(shape);
        }

        private static InputSystemCursorShape HandleCursor(int handle)
        {
            // Handles: 0=TL 1=T 2=TR 3=R 4=BR 5=B 6=BL 7=L
            return handle switch
            {
                0 or 4 => InputSystemCursorShape.SizeNorthwestSoutheast,
                2 or 6 => InputSystemCursorShape.SizeNortheastSouthwest,
                1 or 5 => InputSystemCursorShape.SizeNorthSouth,
                3 or 7 => InputSystemCursorShape.SizeWestEast,
                _ => InputSystemCursorShape.SizeAll,
            };
        }

        private void UpdateVisuals()
        {
            double w = _widthDip;
            double h = _heightDip;

            bool showSelection = _hasSelection || (_mode == Mode.Drawing && _selW > 0 && _selH > 0);

            double sx = showSelection ? _selX : 0;
            double sy = showSelection ? _selY : 0;
            double sw = showSelection ? _selW : 0;
            double sh = showSelection ? _selH : 0;

            // Dim everything except the selection hole using four mask rectangles.
            SetRect(_maskTop, 0, 0, w, sy);
            SetRect(_maskBottom, 0, sy + sh, w, Math.Max(0, h - (sy + sh)));
            SetRect(_maskLeft, 0, sy, sx, sh);
            SetRect(_maskRight, sx + sw, sy, Math.Max(0, w - (sx + sw)), sh);

            if (showSelection)
            {
                _selectionBorder.Visibility = Visibility.Visible;
                SetRect(_selectionBorder, sx, sy, sw, sh);

                var centers = HandleCenters();
                bool showHandles = _hasSelection;
                for (int i = 0; i < _handles.Length; i++)
                {
                    var handle = _handles[i];
                    handle.Visibility = showHandles ? Visibility.Visible : Visibility.Collapsed;
                    if (showHandles)
                    {
                        Canvas.SetLeft(handle, centers[i].X - (HandleSize / 2.0));
                        Canvas.SetTop(handle, centers[i].Y - (HandleSize / 2.0));
                    }
                }
            }
            else
            {
                _selectionBorder.Visibility = Visibility.Collapsed;
                foreach (var handle in _handles)
                {
                    handle.Visibility = Visibility.Collapsed;
                }
            }

            _hint.Visibility = _hasSelection ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void SetRect(Rectangle rect, double x, double y, double width, double height)
        {
            rect.Width = Math.Max(0, width);
            rect.Height = Math.Max(0, height);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (max < min)
            {
                return min;
            }

            return Math.Clamp(value, min, max);
        }

        private async Task CopyAndCloseAsync()
        {
            try
            {
                await CopySelectionToClipboardAsync().ConfigureAwait(true);
                _copied = true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Screenshot: copy failed - {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CloseOverlay();
            }
        }

        private async Task CopySelectionToClipboardAsync()
        {
            int px = (int)Math.Round(_selX * _scale);
            int py = (int)Math.Round(_selY * _scale);
            int pr = (int)Math.Round((_selX + _selW) * _scale);
            int pb = (int)Math.Round((_selY + _selH) * _scale);

            px = Math.Clamp(px, 0, _capture.Width);
            py = Math.Clamp(py, 0, _capture.Height);
            pr = Math.Clamp(pr, 0, _capture.Width);
            pb = Math.Clamp(pb, 0, _capture.Height);

            int pw = pr - px;
            int ph = pb - py;
            if (pw <= 0 || ph <= 0)
            {
                return;
            }

            var cropped = CropBgra(_capture, px, py, pw, ph);

            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)pw,
                (uint)ph,
                96,
                96,
                cropped);
            await encoder.FlushAsync();
            stream.Seek(0);

            var streamRef = RandomAccessStreamReference.CreateFromStream(stream);

            // Clipboard APIs require the STA UI thread. The await chain above may have resumed on a
            // thread-pool thread (the accelerator callback has no SynchronizationContext), so marshal
            // the clipboard write back onto the window's dispatcher.
            await SetClipboardBitmapAsync(streamRef).ConfigureAwait(true);
        }

        private Task SetClipboardBitmapAsync(RandomAccessStreamReference streamRef)
        {
            var tcs = new TaskCompletionSource();
            var dispatcher = DispatcherQueue;

            void Apply()
            {
                try
                {
                    var dataPackage = new DataPackage
                    {
                        RequestedOperation = DataPackageOperation.Copy,
                    };
                    dataPackage.SetBitmap(streamRef);
                    Clipboard.SetContent(dataPackage);
                    Clipboard.Flush();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                Apply();
            }
            else if (!dispatcher.TryEnqueue(Apply))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue clipboard write."));
            }

            return tcs.Task;
        }

        private static byte[] CropBgra(CapturedBitmap capture, int x, int y, int width, int height)
        {
            int srcStride = capture.Width * 4;
            int dstStride = width * 4;
            var dst = new byte[dstStride * height];

            for (int row = 0; row < height; row++)
            {
                int srcOffset = ((y + row) * srcStride) + (x * 4);
                int dstOffset = row * dstStride;
                Array.Copy(capture.Bgra, srcOffset, dst, dstOffset, dstStride);
            }

            return dst;
        }

        private static WriteableBitmap BuildBitmap(CapturedBitmap capture)
        {
            var wb = new WriteableBitmap(capture.Width, capture.Height);
            using (var stream = wb.PixelBuffer.AsStream())
            {
                stream.Write(capture.Bgra, 0, capture.Bgra.Length);
            }

            return wb;
        }

        private static Rectangle NewMask()
        {
            return new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                IsHitTestVisible = false,
            };
        }

        private void CloseOverlay()
        {
            try
            {
                Close();
            }
            catch
            {
            }
        }

        public bool Copied => _copied;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
    }

    // Grid subclass that exposes ProtectedCursor (protected on UIElement) so the overlay can show
    // crosshair / move / resize cursors based on the current interaction.
    internal sealed partial class CursorGrid : Grid
    {
        public void SetCursorShape(InputSystemCursorShape shape)
        {
            try
            {
                this.ProtectedCursor = InputSystemCursor.Create(shape);
            }
            catch
            {
            }
        }
    }
}
