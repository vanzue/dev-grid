// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Logging;
using TopToolbar.Services;
using TopToolbar.ViewModels;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    internal sealed class ToastWindow : WindowEx, IDisposable
    {
        private const int MinToastWidthPx = 360;
        private const int MaxToastWidthPx = 960;
        private const int WindowPaddingPx = 8;
        private const int EdgeMarginPx = 20;

        private static readonly IntPtr HwndTopMost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private readonly NotificationService _notificationService;
        private readonly Grid _root;
        private readonly StackPanel _toastStack;

        private Brush _toastBackground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFC, 0xF7, 0xF1));
        private Brush _toastBorder = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private Brush _toastLabel = new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0x3A, 0x3F));
        private Brush _toastAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
        private FontFamily _textFontFamily = new("Segoe UI Variable Text");

        private PointInt32 _anchorPosition;
        private SizeInt32 _anchorSize;
        private bool _anchorVisible;
        private int _activePromptCount;
        private bool _disposed;
        private string _lastAnchorSignature = string.Empty;
        private string _lastPlacementSignature = string.Empty;

        public ToastWindow(NotificationService notificationService)
        {
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));

            Title = string.Empty;
            IsTitleBarVisible = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            _toastStack = new StackPanel
            {
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Margin = new Thickness(EdgeMarginPx, EdgeMarginPx, EdgeMarginPx, EdgeMarginPx),
            };

            _root = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _root.Children.Add(_toastStack);
            Content = _root;

            ConfigureAppWindowChrome();
            _notificationService.Items.CollectionChanged += OnNotificationCollectionChanged;

            Activate();
            ConfigureAppWindowChrome();
            AppWindow.Hide();
        }

        public void ApplyToolbarThemeResources(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            _toastBackground = CloneBrush(ReadBrush(resources, "ToolbarBackgroundBrush"))
                ?? _toastBackground;
            _toastBorder = CloneBrush(ReadBrush(resources, "ToolbarBorderBrush"))
                ?? _toastBorder;
            _toastLabel = CloneBrush(ReadBrush(resources, "ToolbarLabelBrush"))
                ?? _toastLabel;
            _toastAccent = CloneBrush(ReadBrush(resources, "ToolbarNotificationAccentBrush"))
                ?? _toastAccent;

            if (resources.TryGetValue("ToolbarTextFontFamily", out var fontObj) &&
                fontObj is FontFamily fontFamily &&
                !string.IsNullOrWhiteSpace(fontFamily.Source))
            {
                _textFontFamily = new FontFamily(fontFamily.Source);
            }

            RebuildToastVisuals();
            UpdatePlacement();
        }

        public void UpdateAnchor(PointInt32 position, SizeInt32 size, bool anchorVisible)
        {
            _anchorPosition = position;
            _anchorSize = size;
            _anchorVisible = anchorVisible;

            var anchorSignature = $"{position.X},{position.Y},{size.Width},{size.Height},{anchorVisible}";
            if (!string.Equals(_lastAnchorSignature, anchorSignature, StringComparison.Ordinal))
            {
                _lastAnchorSignature = anchorSignature;
                AppLogger.LogInfo(
                    $"ToastWindow.Anchor: x={position.X}, y={position.Y}, w={size.Width}, h={size.Height}, visible={anchorVisible}");
            }

            UpdatePlacement();
        }

        public Task<string> ShowInputPromptAsync(
            string title,
            string prompt,
            string placeholder,
            string initialValue = "")
        {
            var dispatcher = DispatcherQueue;
            AppLogger.LogInfo(
                $"ToastWindow.Prompt: ShowInputPromptAsync called. dispatcherNull={dispatcher == null}, hasThreadAccess={dispatcher?.HasThreadAccess ?? true}");

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                return ShowInputPromptCoreAsync(title, prompt, placeholder, initialValue);
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var value = await ShowInputPromptCoreAsync(title, prompt, placeholder, initialValue)
                        .ConfigureAwait(true);
                    tcs.TrySetResult(value);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                AppLogger.LogWarning("ToastWindow.Prompt: failed to enqueue prompt on dispatcher.");
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _notificationService.Items.CollectionChanged -= OnNotificationCollectionChanged;
            try
            {
                Close();
            }
            catch
            {
            }
        }

        private async Task<string> ShowInputPromptCoreAsync(
            string title,
            string prompt,
            string placeholder,
            string initialValue)
        {
            _activePromptCount++;
            try
            {
                UpdatePlacement();
                AppLogger.LogInfo(
                    $"ToastWindow.Prompt: open requested. activePrompts={_activePromptCount}, anchor=({_anchorPosition.X},{_anchorPosition.Y},{_anchorSize.Width},{_anchorSize.Height}), anchorVisible={_anchorVisible}");

                var valueBox = new TextBox
                {
                    PlaceholderText = placeholder ?? string.Empty,
                    Text = initialValue ?? string.Empty,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 240,
                };

                var errorText = new TextBlock
                {
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                    Visibility = Visibility.Collapsed,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                };

                var content = new StackPanel { Spacing = 10 };
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = prompt.Trim(),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                content.Children.Add(valueBox);
                content.Children.Add(errorText);

                using var overlay = await TransparentOverlayHost.CreateAsync(this, fullscreen: true)
                    .ConfigureAwait(true);
                if (overlay == null || overlay.Root?.XamlRoot == null)
                {
                    AppLogger.LogWarning("ToastWindow.Prompt: overlay host creation failed.");
                    return null;
                }

                var overlayPos = overlay.Host?.AppWindow?.Position ?? new PointInt32(0, 0);
                var overlaySize = overlay.Host?.AppWindow?.Size ?? new SizeInt32(0, 0);
                var rootScale = overlay.Root.XamlRoot.RasterizationScale <= 0 ? 1d : overlay.Root.XamlRoot.RasterizationScale;
                var rootWidth = overlaySize.Width > 0 ? overlaySize.Width / rootScale : overlay.Root.XamlRoot.Size.Width;
                var rootHeight = overlaySize.Height > 0 ? overlaySize.Height / rootScale : overlay.Root.XamlRoot.Size.Height;
                if (rootWidth > 0 && rootHeight > 0)
                {
                    overlay.Root.Width = rootWidth;
                    overlay.Root.Height = rootHeight;
                }

                AppLogger.LogInfo(
                    $"ToastWindow.Prompt: overlay ready. pos=({overlayPos.X},{overlayPos.Y}), size=({overlaySize.Width},{overlaySize.Height}), rootLoaded={overlay.Root.IsLoaded}, xamlRootNull={overlay.Root.XamlRoot == null}, rootSize=({overlay.Root.Width:0.##},{overlay.Root.Height:0.##})");

                var resultSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completionState = 0;
                var scrim = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)),
                };
                if (rootWidth > 0 && rootHeight > 0)
                {
                    scrim.Width = rootWidth;
                    scrim.Height = rootHeight;
                }

                var card = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 340,
                    MaxWidth = 520,
                    Padding = new Thickness(16),
                    CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(1),
                    Background = CloneBrush(_toastBackground) ?? new SolidColorBrush(Color.FromArgb(0xEA, 0xFC, 0xF7, 0xF1)),
                    BorderBrush = CloneBrush(_toastBorder) ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                };

                var cardStack = new StackPanel { Spacing = 12 };
                cardStack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(title) ? "Input required" : title.Trim(),
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = _toastLabel,
                    FontFamily = _textFontFamily,
                    TextWrapping = TextWrapping.Wrap,
                });
                cardStack.Children.Add(content);

                var actionRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };

                var okButton = new Button
                {
                    Content = "OK",
                    MinWidth = 86,
                    IsEnabled = !string.IsNullOrWhiteSpace(valueBox.Text),
                };

                var cancelButton = new Button
                {
                    Content = "Cancel",
                    MinWidth = 86,
                };

                actionRow.Children.Add(cancelButton);
                actionRow.Children.Add(okButton);
                cardStack.Children.Add(actionRow);
                card.Child = cardStack;
                scrim.Children.Add(card);
                overlay.Root.Children.Add(scrim);
                overlay.Root.UpdateLayout();
                scrim.UpdateLayout();
                card.UpdateLayout();

                valueBox.TextChanged += (_, __) =>
                {
                    var hasText = !string.IsNullOrWhiteSpace(valueBox.Text);
                    okButton.IsEnabled = hasText;
                    if (hasText && errorText.Visibility == Visibility.Visible)
                    {
                        errorText.Visibility = Visibility.Collapsed;
                    }
                };

                okButton.Click += (_, __) =>
                {
                    if (string.IsNullOrWhiteSpace(valueBox.Text))
                    {
                        AppLogger.LogInfo("ToastWindow.Prompt: OK clicked with empty value.");
                        errorText.Text = "Value is required.";
                        errorText.Visibility = Visibility.Visible;
                        return;
                    }

                    if (Interlocked.Exchange(ref completionState, 1) != 0)
                    {
                        return;
                    }

                    okButton.IsEnabled = false;
                    cancelButton.IsEnabled = false;
                    AppLogger.LogInfo($"ToastWindow.Prompt: OK clicked with value='{valueBox.Text.Trim()}'.");
                    resultSource.TrySetResult(valueBox.Text.Trim());
                };

                cancelButton.Click += (_, __) =>
                {
                    if (Interlocked.Exchange(ref completionState, 1) != 0)
                    {
                        return;
                    }

                    okButton.IsEnabled = false;
                    cancelButton.IsEnabled = false;
                    AppLogger.LogInfo("ToastWindow.Prompt: Cancel clicked.");
                    resultSource.TrySetResult(null);
                };

                card.Loaded += (_, __) =>
                {
                    var overlayWindowPos = overlay.Host?.AppWindow?.Position ?? new PointInt32(0, 0);
                    var overlayWindowSize = overlay.Host?.AppWindow?.Size ?? new SizeInt32(0, 0);
                    AppLogger.LogInfo(
                        $"ToastWindow.Prompt: prompt card loaded. overlayPos=({overlayWindowPos.X},{overlayWindowPos.Y}), overlaySize=({overlayWindowSize.Width},{overlayWindowSize.Height}), textBoxLoaded={valueBox.IsLoaded}, textBoxSize=({valueBox.ActualWidth:0.##},{valueBox.ActualHeight:0.##}), cardSize=({card.ActualWidth:0.##},{card.ActualHeight:0.##})");
                    valueBox.Focus(FocusState.Programmatic);
                };

                var promptResult = await resultSource.Task.ConfigureAwait(true);
                AppLogger.LogInfo($"ToastWindow.Prompt: prompt result={(promptResult == null ? "Cancel" : "OK")}.");
                await RunOnUiThreadAsync(() =>
                {
                    if (overlay.Root?.Children == null)
                    {
                        return;
                    }

                    var index = overlay.Root.Children.IndexOf(scrim);
                    if (index >= 0)
                    {
                        overlay.Root.Children.RemoveAt(index);
                    }

                    try
                    {
                        overlay.Host?.Close();
                        AppLogger.LogInfo("ToastWindow.Prompt: overlay host closed.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"ToastWindow.Prompt: overlay close failed - {ex.Message}");
                    }
                }).ConfigureAwait(false);

                return promptResult;
            }
            finally
            {
                _activePromptCount = Math.Max(0, _activePromptCount - 1);
                AppLogger.LogInfo($"ToastWindow.Prompt: closed. activePrompts={_activePromptCount}");
                UpdatePlacement();
            }
        }

        private void OnNotificationCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                RebuildToastVisuals();
                UpdatePlacement();
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RebuildToastVisuals();
                UpdatePlacement();
            });
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            var dispatcher = DispatcherQueue;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
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
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI operation."));
            }

            return tcs.Task;
        }

        private void RebuildToastVisuals()
        {
            _toastStack.Children.Clear();

            foreach (var item in _notificationService.Items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Message))
                {
                    continue;
                }

                _toastStack.Children.Add(CreateToastCard(item));
            }
        }

        private Border CreateToastCard(NotificationItem item)
        {
            var accentBrush = CreateAccentForKind(item.Kind);
            var iconGlyph = item.Kind switch
            {
                NotificationKind.Error => "\uEA39",
                NotificationKind.Warning => "\uE7BA",
                _ => "\uE783",
            };

            var body = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                VerticalAlignment = VerticalAlignment.Center,
            };

            body.Children.Add(new Border
            {
                Width = 5,
                Height = 22,
                CornerRadius = new CornerRadius(2),
                Background = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var icon = new FontIcon
            {
                Glyph = iconGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = accentBrush,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 1);
            body.Children.Add(icon);

            var messageText = new TextBlock
            {
                Text = item.Message,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = _toastLabel,
                FontSize = 14,
                FontFamily = _textFontFamily,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(messageText, 2);
            body.Children.Add(messageText);

            return new Border
            {
                Background = _toastBackground,
                BorderBrush = _toastBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                MaxWidth = MaxToastWidthPx,
                MinWidth = MinToastWidthPx,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = body,
            };
        }

        private Brush CreateAccentForKind(NotificationKind kind)
        {
            return kind switch
            {
                NotificationKind.Error => new SolidColorBrush(Color.FromArgb(0xFF, 0xD1, 0x34, 0x38)),
                NotificationKind.Warning => new SolidColorBrush(Color.FromArgb(0xFF, 0xE7, 0x9E, 0x3A)),
                _ => CloneBrush(_toastAccent) ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0x8A, 0xC9)),
            };
        }

        private void ConfigureAppWindowChrome()
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
                AppLogger.LogWarning($"ToastWindow: failed to configure chrome - {ex.Message}");
            }
        }

        private void UpdatePlacement()
        {
            if (_disposed)
            {
                return;
            }

            var dispatcher = DispatcherQueue;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                _ = dispatcher.TryEnqueue(UpdatePlacement);
                return;
            }

            try
            {
                if (_disposed || AppWindow == null || _root.XamlRoot == null)
                {
                    LogPlacement(
                        "skip",
                        $"disposed={_disposed}, appWindowNull={AppWindow == null}, xamlRootNull={_root?.XamlRoot == null}, items={_notificationService.Items.Count}, prompts={_activePromptCount}");
                    return;
                }

                var hasNotifications = _notificationService.Items.Count > 0;
                var shouldShow = hasNotifications && _activePromptCount == 0;
                if (!shouldShow)
                {
                    AppWindow.Hide();
                    try
                    {
                        AppWindow.Move(new PointInt32(-32000, -32000));
                    }
                    catch
                    {
                    }

                    LogPlacement(
                        "hidden",
                        $"items={_notificationService.Items.Count}, prompts={_activePromptCount}, anchor=({_anchorPosition.X},{_anchorPosition.Y},{_anchorSize.Width},{_anchorSize.Height}), visible={_anchorVisible}");
                    return;
                }

                var scale = _root.XamlRoot.RasterizationScale <= 0 ? 1.0 : _root.XamlRoot.RasterizationScale;
                var display = ResolveDisplayArea();
                var workArea = display?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
                var widthBudget = Math.Max((int)(workArea.Width * 0.42), _anchorSize.Width + 120);
                var maxToastWidth = Math.Clamp(widthBudget, MinToastWidthPx, MaxToastWidthPx);

                foreach (var child in _toastStack.Children)
                {
                    if (child is Border toast)
                    {
                        toast.MaxWidth = maxToastWidth;
                    }
                }

                AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
                AppWindow.Resize(new SizeInt32(workArea.Width, workArea.Height));
                _root.Width = workArea.Width / scale;
                _root.Height = workArea.Height / scale;
                AppWindow.Show(false);
                MakeTopMost();
                var actualPos = AppWindow.Position;
                var actualSize = AppWindow.Size;
                LogPlacement(
                    "shown",
                    $"x={workArea.X}, y={workArea.Y}, w={workArea.Width}, h={workArea.Height}, actual=({actualPos.X},{actualPos.Y},{actualSize.Width},{actualSize.Height}), mode=bottom-right, toastMaxW={maxToastWidth}, anchor=({_anchorPosition.X},{_anchorPosition.Y},{_anchorSize.Width},{_anchorSize.Height}), anchorVisible={_anchorVisible}, items={_notificationService.Items.Count}, prompts={_activePromptCount}, scale={scale:0.###}");
            }
            catch (COMException ex)
            {
                AppLogger.LogWarning($"ToastWindow.UpdatePlacement: COM exception hr=0x{ex.HResult:X8}, message={ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"ToastWindow.UpdatePlacement: exception - {ex.Message}");
            }
        }

        private void LogPlacement(string phase, string details)
        {
            var signature = $"{phase}|{details}";
            if (string.Equals(_lastPlacementSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastPlacementSignature = signature;
            AppLogger.LogInfo($"ToastWindow.Placement[{phase}]: {details}");
        }

        private DisplayArea ResolveDisplayArea()
        {
            try
            {
                var width = Math.Max(_anchorSize.Width, 1);
                var height = Math.Max(_anchorSize.Height, 1);
                var rect = new RectInt32(_anchorPosition.X, _anchorPosition.Y, width, height);
                var byRect = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
                if (byRect != null)
                {
                    return byRect;
                }
            }
            catch
            {
            }

            try
            {
                var probe = new PointInt32(
                    _anchorPosition.X + Math.Max(_anchorSize.Width / 2, 1),
                    _anchorPosition.Y + Math.Max(_anchorSize.Height / 2, 1));
                var byPoint = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.None);
                if (byPoint != null)
                {
                    return byPoint;
                }
            }
            catch
            {
            }

            return DisplayArea.GetFromPoint(new PointInt32(1, 1), DisplayAreaFallback.Primary);
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

        private static Brush ReadBrush(ResourceDictionary resources, string key)
        {
            if (resources == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (resources.TryGetValue(key, out var value) && value is Brush brush)
            {
                return brush;
            }

            return null;
        }

        private static Brush CloneBrush(Brush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                return new SolidColorBrush(solid.Color);
            }

            if (brush is RadialGradientBrush radial)
            {
                var clone = new RadialGradientBrush
                {
                    Center = radial.Center,
                    GradientOrigin = radial.GradientOrigin,
                    RadiusX = radial.RadiusX,
                    RadiusY = radial.RadiusY,
                    SpreadMethod = radial.SpreadMethod,
                };

                foreach (var stop in radial.GradientStops)
                {
                    clone.GradientStops.Add(new GradientStop { Color = stop.Color, Offset = stop.Offset });
                }

                return clone;
            }

            return null;
        }

        private static async Task EnsureXamlRootAsync(FrameworkElement element)
        {
            if (element == null || element.XamlRoot != null)
            {
                return;
            }

            if (element.IsLoaded)
            {
                element.UpdateLayout();
                return;
            }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = null;
            handler = (_, __) =>
            {
                element.Loaded -= handler;
                tcs.TrySetResult(null);
            };

            element.Loaded += handler;
            await tcs.Task.ConfigureAwait(true);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
    }
}
