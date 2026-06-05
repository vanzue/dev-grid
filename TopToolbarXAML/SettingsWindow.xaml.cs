// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TopToolbar.Providers;
using TopToolbar.Services;
using TopToolbar.ViewModels;

namespace TopToolbar
{
    public sealed partial class SettingsWindow : WinUIEx.WindowEx, IDisposable
    {
        private readonly SettingsViewModel _vm;
        private readonly ActionProviderRuntime _providerRuntime;
        private bool _isClosed;
        private bool _disposed;
        private FrameworkElement _appTitleBarCache;
        private ColumnDefinition _leftPaneColumnCache;
        private DateTime _lastLoadedConfigWriteUtc = DateTime.MinValue;

        public SettingsViewModel ViewModel => _vm;

        public SettingsWindow(ActionProviderRuntime providerRuntime = null)
        {
            InitializeComponent();

            _providerRuntime = providerRuntime;
            _vm = new SettingsViewModel(new ToolbarConfigService());
            InitializeWindowStyling();
            LayoutRoot.DataContext = _vm;
            this.Closed += async (s, e) =>
            {
                await _vm.SaveAsync();
            };
            this.Activated += async (s, e) =>
            {
                if (ShouldReloadToolbarConfig())
                {
                    await _vm.LoadAsync(this.DispatcherQueue);
                    _lastLoadedConfigWriteUtc = GetToolbarConfigWriteUtc();
                }

                // Load startup state when window activates
                await _vm.LoadStartupStateAsync();
            };

            // Keep left pane visible when no selection so UI doesn't look empty
            _vm.PropertyChanged += ViewModel_PropertyChanged;

            // Modern styling applied via InitializeWindowStyling
        }

        private void InitializeWindowStyling()
        {
            // Try set Mica backdrop (Base for subtle tint)
            try
            {
                var mica = new Microsoft.UI.Xaml.Media.MicaBackdrop
                {
                    Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base,
                };
                SystemBackdrop = mica;
            }
            catch { }

            // Extend into title bar & customize caption buttons
            try
            {
                if (AppWindow?.TitleBar != null)
                {
                    var tb = AppWindow.TitleBar;
                    tb.ExtendsContentIntoTitleBar = true;
                    tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
                    tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 0, 0, 0);
                    tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(36, 0, 0, 0);
                }

                _appTitleBarCache ??= GetAppTitleBar();
                if (_appTitleBarCache is FrameworkElement dragRegion)
                {
                    this.SetTitleBar(dragRegion);
                }
            }
            catch { }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (
                e.PropertyName == nameof(SettingsViewModel.SelectedGroup)
                || e.PropertyName == nameof(SettingsViewModel.HasNoSelectedGroup)
                || e.PropertyName == nameof(SettingsViewModel.SelectedWorkspace)
            )
            {
                EnsureLeftPaneColumn();
                _leftPaneColumnCache ??= GetLeftPaneColumn();
                if (_leftPaneColumnCache != null)
                {
                    // Keep navigation pane width stable while switching settings sections.
                    _leftPaneColumnCache.Width = new GridLength(292);
                }
            }
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            await _vm.SaveAsync();
        }

        private async void OnClose(object sender, RoutedEventArgs e)
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true; // prevent re-entry

            try
            {
                await _vm.SaveAsync();
            }
            catch (Exception ex)
            {
                try
                {
                    SafeLogWarning($"Save before close failed: {ex.Message}");
                }
                catch { }
            }

            SafeCloseWindow();
        }

        private async void OnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                // Avoid reacting to programmatic changes
                if (toggle.IsOn == _vm.IsStartupEnabled)
                {
                    return;
                }

                var success = await _vm.SetStartupEnabledAsync(toggle.IsOn);
                if (!success)
                {
                    // Revert toggle if operation failed
                    toggle.IsOn = _vm.IsStartupEnabled;
                }
            }
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
                _vm?.Dispose();
            }
            catch { }
        }

        private void ToggleMaximize()
        {
            try
            {
                if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                {
                    if (p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                    {
                        p.Restore();
                    }
                    else
                    {
                        p.Maximize();
                    }
                }
            }
            catch { }
        }

        private void EnsureLeftPaneColumn()
        {
            if (_leftPaneColumnCache == null)
            {
                _leftPaneColumnCache = GetLeftPaneColumn();
            }
        }

        private ColumnDefinition GetLeftPaneColumn()
        {
            try
            {
                // The left pane ColumnDefinition has x:Name="LeftPaneColumn" in XAML. Generated partial may expose field; if not, locate via visual tree.
                var root = this.Content as FrameworkElement;
                if (root != null)
                {
                    return (ColumnDefinition)root.FindName("LeftPaneColumn");
                }
            }
            catch { }

            return null;
        }

        private FrameworkElement GetAppTitleBar()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root != null)
                {
                    return root.FindName("AppTitleBar") as FrameworkElement;
                }
            }
            catch { }

            return null;
        }

        private bool ShouldReloadToolbarConfig()
        {
            if (_vm.Groups.Count == 0)
            {
                return true;
            }

            var currentWriteUtc = GetToolbarConfigWriteUtc();
            return currentWriteUtc > _lastLoadedConfigWriteUtc;
        }

        private static DateTime GetToolbarConfigWriteUtc()
        {
            try
            {
                var path = AppPaths.ConfigFile;
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    return System.IO.File.GetLastWriteTimeUtc(path);
                }
            }
            catch
            {
            }

            return DateTime.MinValue;
        }

        // Removed manual BeginDragMove implementation: using SetTitleBar now.
        private void SafeCloseWindow()
        {
            try
            {
                Close();
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                // Ignore RO_E_CLOSED or already closed window scenarios
                try
                {
                    SafeLogWarning($"Close COMException 0x{comEx.HResult:X}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                try
                {
                    SafeLogError($"Unexpected Close exception: {ex.Message}");
                }
                catch { }
            }
        }

        private static void SafeLogWarning(string msg)
        {
#if HAS_MANAGEDCOMMON_LOGGER
            try
            {
                ManagedCommon.Logger.LogWarning("SettingsWindow: " + msg);
            }
            catch { }
#else
            Debug.WriteLine("[SettingsWindow][WARN] " + msg);
#endif
        }

        private static void SafeLogError(string msg)
        {
#if HAS_MANAGEDCOMMON_LOGGER
            try
            {
                ManagedCommon.Logger.LogError("SettingsWindow: " + msg);
            }
            catch { }
#else
            Debug.WriteLine("[SettingsWindow][ERR ] " + msg);
#endif
        }
    }
}
