// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TopToolbar.Actions;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Services;
using TopToolbar.ViewModels;
using WinUIEx;
using Timer = System.Timers.Timer;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow : WindowEx, IDisposable
    {
        private const int TriggerZoneHeight = 2;
        private readonly ToolbarConfigService _configService;
        private readonly ActionProviderRuntime _providerRuntime;
        private readonly ActionProviderService _providerService;
        private readonly ActionContextFactory _contextFactory;
        private readonly ToolbarActionExecutor _actionExecutor;
        private readonly BuiltinProvider _builtinProvider;
        private readonly ToolbarViewModel _vm;
        private readonly NotificationService _notificationService;

        private readonly TopToolbar.Stores.ToolbarStore _store = new();
        public ToolbarItemsViewModel ItemsViewModel { get; }
        public NotificationService NotificationService => _notificationService;
        private Timer _monitorTimer;
        private Timer _configWatcherDebounce;
        private bool _isVisible;
        private bool _requireCtrlForTopBarTrigger;
        private int _topBarTriggerWidth = 320;
        private bool _builtConfigOnce;
        private IntPtr _hwnd;
        private bool _initializedLayout;
        private FileSystemWatcher _configWatcher;
        private IntPtr _oldWndProc;
        private DpiWndProcDelegate _newWndProc;
        private SettingsWindow _settingsWindow;
        private PropertyChangedEventHandler _settingsViewModelPropertyChangedHandler;

        private bool _snapshotInProgress;

        private delegate IntPtr DpiWndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public ToolbarWindow()
        {
            _configService = new ToolbarConfigService();
            _contextFactory = new ActionContextFactory();
            _providerRuntime = new ActionProviderRuntime();
            _providerService = new ActionProviderService(_providerRuntime);
            _notificationService = new NotificationService(DispatcherQueue);
            _actionExecutor = new ToolbarActionExecutor(_providerService, _contextFactory, DispatcherQueue, _notificationService);
            _builtinProvider = new BuiltinProvider();
            _vm = new ToolbarViewModel(_configService, _providerService, _contextFactory);
            ItemsViewModel = new ToolbarItemsViewModel(_store);
            ItemsViewModel.LayoutChanged += (_, __) =>
            {
                if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
                {
                    ResizeToContent();
                    if (!_isVisible)
                    {
                        PositionAtTopCenter();
                    }
                }
                else
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        ResizeToContent();
                        if (!_isVisible)
                        {
                            PositionAtTopCenter();
                        }
                    });
                }
            };

            InitializeComponent();
            EnsurePerMonitorV2();
            RegisterProviders();

            _providerRuntime.ProvidersChanged += async (_, args) =>
            {
                if (args == null)
                {
                    return;
                }

                if (!IsRegisteredGroupProvider(args.ProviderId))
                {
                    return;
                }

                try
                {
                    var kindsNeedingGroup = args.Kind == ProviderChangeKind.ActionsUpdated ||
                                            args.Kind == ProviderChangeKind.ActionsAdded ||
                                            args.Kind == ProviderChangeKind.ActionsRemoved ||
                                            args.Kind == ProviderChangeKind.GroupUpdated ||
                                            args.Kind == ProviderChangeKind.BulkRefresh ||
                                            args.Kind == ProviderChangeKind.Reset ||
                                            args.Kind == ProviderChangeKind.ProviderRegistered;

                    if (!kindsNeedingGroup)
                    {
                        return; // other change kinds (progress, execution) not yet surfaced
                    }

                    await RefreshProviderGroupAsync(args.ProviderId, CancellationToken.None);
                }
                catch (Exception)
                {
                    // TODO: log: provider change handling wrapper failure
                }
            };

            Title = "Workspace Center";

            // Make window background completely transparent
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));

            // Apply styles immediately after activation as backup
            this.Activated += (s, e) => MakeTopMost();

            StartMonitoring();
            StartWatchingConfig();

            // Load config and build UI when window activates
            this.Activated += async (s, e) =>
            {
                if (_builtConfigOnce)
                {
                    return;
                }

                await _vm.LoadAsync(this.DispatcherQueue);
                await RunOnUiThreadAsync(SyncStaticGroupsIntoStore);
                await RefreshDynamicProviderGroupsAsync(CancellationToken.None);
                await EnsureQuickTemplatesLoadedAsync(forceReload: true);

                await RunOnUiThreadAsync(() =>
                {
                    ApplyTheme(_vm.Theme);
                    _requireCtrlForTopBarTrigger = _vm.RequireCtrlForTopBarTrigger;
                    ApplyDisplayMode(_vm.DisplayMode);
                    if (_currentDisplayMode == ToolbarDisplayMode.TopBar)
                    {
                        ResizeToContent();
                        PositionAtTopCenter();
                    }

                    ToolbarScrollViewer?.ChangeView(0, null, null, disableAnimation: true);
                    _builtConfigOnce = true;
                });
            };
        }

        public void Dispose()
        {
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            _configWatcherDebounce?.Stop();
            _configWatcherDebounce?.Dispose();
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
            }

            UnregisterRadialHotKey();
            StopRadialHotKeyFallbackPolling(disposeTimer: true);

            try
            {
                ItemsViewModel?.Dispose();
            }
            catch
            {
            }

            // Dispose the built-in provider which handles all provider disposals
            try
            {
                _builtinProvider?.Dispose();
            }
            catch (Exception)
            {
            }

            GC.SuppressFinalize(this);
        }

        private void ToolbarContainer_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initializedLayout)
            {
                return;
            }

            _hwnd = this.GetWindowHandle();
            ApplyTransparentBackground();
            ApplyFramelessStyles();
            TryHookDpiMessages();
            ResizeToContent();
            PositionAtTopCenter();
            AppWindow.Hide();
            _isVisible = false;
            ApplyDisplayMode(_currentDisplayMode);
            _initializedLayout = true;
        }

        private async void OnToolbarButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ToolbarButtonItem item)
            {
                try
                {
                    await _actionExecutor.ExecuteAsync(item.Group, item.Button, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private void OnToolbarButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            OpenSettingsWindow();
        }

        private async void OnSnapshotClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                await HandleSnapshotButtonClickAsync(btn).ConfigureAwait(true);
            }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            OpenSettingsWindow();
        }

        private void OpenSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_providerRuntime);

            _settingsViewModelPropertyChangedHandler = (_, args) =>
            {
                if (args?.PropertyName == nameof(SettingsViewModel.Theme) ||
                    args?.PropertyName == nameof(SettingsViewModel.ThemeIndex))
                {
                    var selectedTheme = _settingsWindow?.ViewModel?.Theme ?? _vm.Theme;
                    if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
                    {
                        ApplyTheme(selectedTheme);
                    }
                    else
                    {
                        _ = DispatcherQueue.TryEnqueue(() => ApplyTheme(selectedTheme));
                    }
                }
            };
            _settingsWindow.ViewModel.PropertyChanged += _settingsViewModelPropertyChangedHandler;

            _settingsWindow.Closed += (_, __) =>
            {
                var closedWindow = _settingsWindow;
                if (closedWindow?.ViewModel != null && _settingsViewModelPropertyChangedHandler != null)
                {
                    closedWindow.ViewModel.PropertyChanged -= _settingsViewModelPropertyChangedHandler;
                }

                _settingsViewModelPropertyChangedHandler = null;
                _settingsWindow = null;

                _ = DispatcherQueue?.TryEnqueue(async () =>
                {
                    await RefreshDynamicProviderGroupsAsync(CancellationToken.None);
                    await EnsureQuickTemplatesLoadedAsync(forceReload: true);
                });
            };
            _settingsWindow.Activate();
        }
    }
}
