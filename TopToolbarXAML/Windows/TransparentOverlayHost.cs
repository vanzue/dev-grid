// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Logging;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    /// <summary>
    /// Provides a reusable transparent overlay window that hosts ad-hoc XAML content.
    /// </summary>
    internal sealed class TransparentOverlayHost : IDisposable
    {
        private readonly bool _ownsHost;

        private TransparentOverlayHost(WindowEx host, Grid root, bool ownsHost)
        {
            Host = host;
            Root = root;
            _ownsHost = ownsHost;
        }

        public WindowEx Host { get; }

        public Grid Root { get; }

        public static async Task<TransparentOverlayHost> CreateAsync(WindowEx owner, bool fullscreen = true)
        {
            var host = new WindowEx
            {
                Title = string.Empty,
                IsTitleBarVisible = false,
                ExtendsContentIntoTitleBar = true,
            };

            host.SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));

            var root = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            host.Content = root;

            if (host.AppWindow is AppWindow appWindow)
            {
                ConfigureAppWindowChrome(owner, appWindow, fullscreen);
            }

            host.Activate();
            await EnsureXamlRootAsync(root).ConfigureAwait(true);

            if (root.XamlRoot == null)
            {
                AppLogger.LogWarning("TransparentOverlayHost: root.XamlRoot was null after activation.");
                host.Close();
                return null;
            }

            try
            {
                var appSize = host.AppWindow?.Size ?? new SizeInt32(0, 0);
                var scale = root.XamlRoot.RasterizationScale <= 0 ? 1d : root.XamlRoot.RasterizationScale;
                if (appSize.Width > 0 && appSize.Height > 0)
                {
                    root.Width = appSize.Width / scale;
                    root.Height = appSize.Height / scale;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"TransparentOverlayHost: failed to size root - {ex.Message}");
            }

            var position = host.AppWindow?.Position ?? new PointInt32(0, 0);
            var size = host.AppWindow?.Size ?? new SizeInt32(0, 0);
            AppLogger.LogInfo(
                $"TransparentOverlayHost: created. fullscreen={fullscreen}, pos=({position.X},{position.Y}), size=({size.Width},{size.Height})");
            return new TransparentOverlayHost(host, root, ownsHost: true);
        }

        public void Dispose()
        {
            if (_ownsHost)
            {
                try
                {
                    Host?.Close();
                }
                catch
                {
                }
            }
        }

        private static void ConfigureAppWindowChrome(WindowEx owner, AppWindow appWindow, bool fullscreen)
        {
            appWindow.IsShownInSwitchers = false;
            appWindow.SetIcon(null);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            try
            {
                DisplayArea displayArea = null;
                if (owner?.AppWindow != null)
                {
                    try
                    {
                        displayArea = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"TransparentOverlayHost: owner window-id lookup failed - {ex.Message}");
                    }

                    if (displayArea == null)
                    {
                        try
                        {
                            var ownerPos = owner.AppWindow.Position;
                            var ownerSize = owner.AppWindow.Size;
                            var probe = new PointInt32(
                                ownerPos.X + Math.Max(ownerSize.Width / 2, 1),
                                ownerPos.Y + Math.Max(ownerSize.Height / 2, 1));
                            displayArea = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.Primary);
                            AppLogger.LogInfo(
                                $"TransparentOverlayHost: owner point fallback probe=({probe.X},{probe.Y}), ownerPos=({ownerPos.X},{ownerPos.Y}), ownerSize=({ownerSize.Width},{ownerSize.Height})");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"TransparentOverlayHost: owner point fallback failed - {ex.Message}");
                        }
                    }
                }

                if (displayArea == null)
                {
                    try
                    {
                        displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"TransparentOverlayHost: host window-id lookup failed - {ex.Message}");
                    }
                }

                displayArea ??= DisplayArea.GetFromPoint(new PointInt32(0, 0), DisplayAreaFallback.Primary);

                if (displayArea != null)
                {
                    var workArea = displayArea.WorkArea;

                    if (fullscreen)
                    {
                        appWindow.Move(new PointInt32(workArea.X, workArea.Y));
                        appWindow.Resize(new SizeInt32(workArea.Width, workArea.Height));
                    }

                    var finalPos = appWindow.Position;
                    var finalSize = appWindow.Size;
                    AppLogger.LogInfo(
                        $"TransparentOverlayHost: configured. fullscreen={fullscreen}, work=({workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}), finalPos=({finalPos.X},{finalPos.Y}), finalSize=({finalSize.Width},{finalSize.Height})");
                }
                else
                {
                    AppLogger.LogWarning("TransparentOverlayHost: unable to resolve display area.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"TransparentOverlayHost: configure failed - {ex.Message}");
            }
        }

        private static async Task EnsureXamlRootAsync(FrameworkElement element)
        {
            if (element.XamlRoot != null)
            {
                return;
            }

            if (element.IsLoaded)
            {
                element.UpdateLayout();
                return;
            }

            var tcs = new TaskCompletionSource<object>();
            RoutedEventHandler handler = null;
            handler = (_, __) =>
            {
                element.Loaded -= handler;
                tcs.TrySetResult(null);
            };

            element.Loaded += handler;
            await tcs.Task.ConfigureAwait(true);
        }
    }
}
