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
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Services;
using TopToolbar.Services.Everything;
using TopToolbar.ViewModels;
using WinUIEx;
using Timer = System.Timers.Timer;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow : WindowEx, IDisposable
    {
        private const int TriggerZoneHeight = 2;
        private const string WorkspaceProviderId = "WorkspaceProvider";
        private const string WorkspaceLaunchActionPrefix = "workspace.launch:";
        private const string WorkspaceButtonIdPrefix = "workspace::";
        private readonly ToolbarConfigService _configService;
        private readonly ActionProviderRuntime _providerRuntime;
        private readonly ActionProviderService _providerService;
        private readonly ActionContextFactory _contextFactory;
        private readonly ToolbarActionExecutor _actionExecutor;
        private readonly BuiltinProvider _builtinProvider;
        private readonly ToolbarViewModel _vm;
        private readonly NotificationService _notificationService;
        private readonly ToastWindow _toastWindow;
        private readonly EverythingSearchService _everythingSearchService;

        private readonly TopToolbar.Stores.ToolbarStore _store = new();
        public ToolbarItemsViewModel ItemsViewModel { get; }
        public NotificationService NotificationService => _notificationService;
        private Timer _monitorTimer;
        private Timer _configWatcherDebounce;
        private bool _isVisible;
        private bool _isContextMenuOpen;
        private bool _requireCtrlForTopBarTrigger;
        private int _topBarTriggerWidth = 320;
        private bool _builtConfigOnce;
        private IntPtr _hwnd;
        private bool _initializedLayout;
        private FileSystemWatcher _configWatcher;
        private IntPtr _oldWndProc;
        private DpiWndProcDelegate _newWndProc;
        private SettingsWindow _settingsWindow;
        private EverythingSearchWindow _everythingSearchWindow;
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
            _toastWindow = new ToastWindow(_notificationService);
            _everythingSearchService = new EverythingSearchService();
            InitializeHotCorners();
            _actionExecutor = new ToolbarActionExecutor(
                _providerService,
                _contextFactory,
                DispatcherQueue,
                _notificationService,
                HandleWorkspaceLaunchFailureAsync);
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

            Title = "Dev Grid";

            // Make window background completely transparent
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));

            // Apply styles immediately after activation as backup.
            this.Activated += (s, e) =>
            {
                if (e.WindowActivationState != WindowActivationState.Deactivated)
                {
                    MakeTopMost();
                }
            };

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

                await RunOnUiThreadAsync(() =>
                {
                    ApplyTheme(_vm.Theme);
                    _requireCtrlForTopBarTrigger = _vm.RequireCtrlForTopBarTrigger;
                    ApplyInvocationModes(_vm.TopBarEnabled, _vm.RadialMenuEnabled, _vm.DisplayMode);
                    if (_topBarEnabled)
                    {
                        ResizeToContent();
                        PositionAtTopCenter();
                    }

                    ToolbarScrollViewer?.ChangeView(0, null, null, disableAnimation: true);
                    _builtConfigOnce = true;
                });

                await ApplyHotCornersConfigAsync();
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

            try
            {
                _toastWindow?.Dispose();
            }
            catch
            {
            }

            try
            {
                _everythingSearchWindow?.Dispose();
                _everythingSearchWindow = null;
                _everythingSearchService?.Dispose();
            }
            catch
            {
            }

            DisposeHotCorners();

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
            ApplyInvocationModes(_topBarEnabled, _radialMenuEnabled, _currentDisplayMode);
            SyncToastWindowTheme();
            UpdateToastWindowAnchor();
            _initializedLayout = true;
        }

        private void SyncToastWindowTheme()
        {
            _toastWindow?.ApplyToolbarThemeResources(RootGrid?.Resources);
            SyncCornerOverlayTheme();
        }

        private void UpdateToastWindowAnchor()
        {
            if (_toastWindow == null)
            {
                return;
            }

            _toastWindow.UpdateAnchor(AppWindow.Position, AppWindow.Size, _isVisible);
        }

        private async void OnToolbarButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ToolbarButtonItem item)
            {
                if (IsScreenshotAction(item.Button))
                {
                    await LaunchScreenshotCaptureAsync();
                    return;
                }

                try
                {
                    if (IsEverythingSearchAction(item.Button?.Action))
                    {
                        OpenEverythingSearchWindow();
                        return;
                    }

                    await _actionExecutor.ExecuteAsync(item.Group, item.Button, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private static bool IsEverythingSearchAction(ToolbarAction action)
        {
            return action != null &&
                   action.Type == ToolbarActionType.Provider &&
                   string.Equals(action.ProviderId, EverythingSearchProvider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(action.ProviderActionId, EverythingSearchProvider.OpenSearchActionId, StringComparison.OrdinalIgnoreCase);
        }

        private void OpenEverythingSearchWindow()
        {
            if (_everythingSearchWindow == null)
            {
                _everythingSearchWindow = new EverythingSearchWindow(_everythingSearchService);
                _everythingSearchWindow.Closed += (_, __) =>
                {
                    _everythingSearchWindow = null;
                };
            }

            _everythingSearchWindow.Activate();
            _everythingSearchWindow.FocusSearchBox();
        }

        private async void OnToolbarButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe ||
                fe.Tag is not ToolbarButtonItem item)
            {
                return;
            }

            e.Handled = true;
            var showPosition = e.GetPosition(fe);

            // Non-workspace actions only get the unified pin toggles (Show on bar / Show on ring).
            if (!TryGetRuntimeWorkspaceId(item.Button, out var workspaceId))
            {
                var actionMenu = new MenuFlyout();
                AddPinMenuItems(actionMenu, item);
                WireContextMenuAutoHide(actionMenu);
                actionMenu.ShowAt(fe, showPosition);
                return;
            }

            var workspaceName = item.Button.DisplayName;

            bool isHot;
            bool isCold;
            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    return;
                }

                isHot = await workspaceProvider.IsHotWorkspaceAsync(workspaceId, CancellationToken.None)
                    .ConfigureAwait(false);
                isCold = !isHot && await workspaceProvider.IsColdWorkspaceAsync(workspaceId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to load workspace actions: " + ex.Message);
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var menu = new MenuFlyout();

                // Unified pin toggles apply to every action, including workspaces.
                AddPinMenuItems(menu, item);
                menu.Items.Add(new MenuFlyoutSeparator());

                var persistItem = new MenuFlyoutItem
                {
                    Text = "Persist as cold workspace",
                };
                persistItem.Click += async (_, _) =>
                {
                    await PersistRuntimeWorkspaceAsync(workspaceId, workspaceName).ConfigureAwait(true);
                };
                var hideItem = new MenuFlyoutItem
                {
                    Text = "Hide workspace windows",
                };
                hideItem.Click += async (_, _) =>
                {
                    await HideRuntimeWorkspaceAsync(workspaceId, workspaceName).ConfigureAwait(true);
                };
                var killItem = new MenuFlyoutItem
                {
                    Text = "Kill workspace windows",
                };
                killItem.Click += async (_, _) =>
                {
                    await KillRuntimeWorkspaceAsync(workspaceId, workspaceName).ConfigureAwait(true);
                };
                var renameItem = new MenuFlyoutItem
                {
                    Text = "Rename workspace",
                };
                renameItem.Click += async (_, _) =>
                {
                    await RenameRuntimeWorkspaceAsync(workspaceId, workspaceName).ConfigureAwait(true);
                };
                var deleteItem = new MenuFlyoutItem
                {
                    Text = "Remove workspace",
                };
                deleteItem.Click += async (_, _) =>
                {
                    await RemoveRuntimeWorkspaceAsync(workspaceId, workspaceName).ConfigureAwait(true);
                };

                if (isHot)
                {
                    menu.Items.Add(persistItem);
                    menu.Items.Add(hideItem);
                    menu.Items.Add(killItem);
                    menu.Items.Add(new MenuFlyoutSeparator());
                    menu.Items.Add(deleteItem);
                }
                else if (isCold)
                {
                    menu.Items.Add(renameItem);
                    menu.Items.Add(deleteItem);
                }
                else
                {
                    menu.Items.Add(deleteItem);
                }

                WireContextMenuAutoHide(menu);
                menu.ShowAt(fe, showPosition);
            }).ConfigureAwait(false);
        }

        // Adds the unified "Show on bar" / "Show on ring" pin toggles for an action.
        private void AddPinMenuItems(MenuFlyout menu, ToolbarButtonItem item)
        {
            if (menu == null || item?.Button == null)
            {
                return;
            }

            var button = item.Button;

            var barItem = new ToggleMenuFlyoutItem
            {
                Text = "Show on bar",
                IsChecked = button.IsPinnedToBar,
            };
            barItem.Click += (s, _) =>
            {
                var toggle = s as ToggleMenuFlyoutItem;
                SetActionSurface(item, ActionSurfaces.Bar, toggle?.IsChecked ?? true, toggle);
            };

            var ringItem = new ToggleMenuFlyoutItem
            {
                Text = "Show on ring",
                IsChecked = button.IsPinnedToRing,
            };
            ringItem.Click += (s, _) =>
            {
                var toggle = s as ToggleMenuFlyoutItem;
                SetActionSurface(item, ActionSurfaces.Ring, toggle?.IsChecked ?? true, toggle);
            };

            menu.Items.Add(barItem);
            menu.Items.Add(ringItem);
        }

        // Applies a pin/unpin to a surface, persists it, and updates the live UI. An action is never
        // allowed to be unpinned from every surface (it would become unreachable), so the last surface
        // toggle is reverted in that case.
        private void SetActionSurface(ToolbarButtonItem item, ActionSurfaces flag, bool on, ToggleMenuFlyoutItem source)
        {
            var button = item?.Button;
            if (button == null)
            {
                return;
            }

            var current = button.Surfaces;
            var next = on ? (current | flag) : (current & ~flag);

            if (next == ActionSurfaces.None)
            {
                if (source != null)
                {
                    source.IsChecked = true;
                }

                return;
            }

            if (next == current)
            {
                return;
            }

            button.Surfaces = next;
            ActionPinStore.Instance.Set(ActionPinStore.GetActionKey(button), next);
        }

        // The context menu can extend below the toolbar window. Suppress auto-hide while it is open so
        // moving the cursor into lower menu items does not hide the toolbar (which would dismiss it).
        private void WireContextMenuAutoHide(MenuFlyout menu)
        {
            if (menu == null)
            {
                return;
            }

            menu.Opened += (_, _) => _isContextMenuOpen = true;
            menu.Closed += (_, _) => _isContextMenuOpen = false;
        }

        private void OnToolbarScrollViewerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer || e == null)
            {
                return;
            }

            var delta = e.GetCurrentPoint(scrollViewer).Properties.MouseWheelDelta;
            if (delta == 0 || scrollViewer.ScrollableWidth <= 0)
            {
                return;
            }

            var step = Math.Max(56d, ToolbarMetrics.ButtonContainerWidth * 0.85d);
            var next = scrollViewer.HorizontalOffset - (Math.Sign(delta) * step);
            if (next < 0)
            {
                next = 0;
            }
            else if (next > scrollViewer.ScrollableWidth)
            {
                next = scrollViewer.ScrollableWidth;
            }

            if (Math.Abs(next - scrollViewer.HorizontalOffset) > 0.5d)
            {
                scrollViewer.ChangeView(next, null, null, disableAnimation: false);
                e.Handled = true;
            }
        }


        private System.Threading.Tasks.Task HandleWorkspaceLaunchFailureAsync(
            ToolbarButton button,
            ToolbarAction action,
            string failureMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (button == null || action == null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (!TryGetRuntimeWorkspaceId(button, out var workspaceId))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var workspaceName = button.DisplayName;
            var message = $"{workspaceName} failed to launch.";

            _notificationService.ShowAction(
                NotificationKind.Error,
                message,
                "Delete",
                () => RemoveRuntimeWorkspaceAsync(workspaceId, workspaceName),
                TimeSpan.FromSeconds(10));

            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static bool TryGetRuntimeWorkspaceId(ToolbarButton button, out string workspaceId)
        {
            workspaceId = string.Empty;
            if (button == null)
            {
                return false;
            }

            var action = button.Action;
            if (action != null &&
                action.Type == ToolbarActionType.Provider &&
                string.Equals(action.ProviderId, WorkspaceProviderId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(action.ProviderActionId) &&
                action.ProviderActionId.StartsWith(WorkspaceLaunchActionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                workspaceId = action.ProviderActionId.Substring(WorkspaceLaunchActionPrefix.Length).Trim();
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                var buttonId = button.Id?.Trim() ?? string.Empty;
                if (buttonId.StartsWith(WorkspaceButtonIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    workspaceId = buttonId.Substring(WorkspaceButtonIdPrefix.Length).Trim();
                }
            }

            return !string.IsNullOrWhiteSpace(workspaceId);
        }

        private async System.Threading.Tasks.Task RemoveRuntimeWorkspaceAsync(
            string workspaceId,
            string workspaceName)
        {
            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return;
            }

            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    _notificationService.ShowError("Workspace provider is unavailable.");
                    return;
                }

                var removed = await workspaceProvider.DeleteWorkspaceAsync(normalizedWorkspaceId, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!removed)
                {
                    _notificationService.ShowInfo("Workspace was already removed.");
                    return;
                }

                await RefreshDynamicProviderGroupsAsync(CancellationToken.None).ConfigureAwait(true);

                var label = string.IsNullOrWhiteSpace(workspaceName)
                    ? normalizedWorkspaceId
                    : workspaceName.Trim();
                _notificationService.ShowSuccess($"Removed workspace '{label}'.");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to remove workspace: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task PersistRuntimeWorkspaceAsync(
            string workspaceId,
            string workspaceName)
        {
            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return;
            }

            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    _notificationService.ShowError("Workspace provider is unavailable.");
                    return;
                }

                var defaultName = string.IsNullOrWhiteSpace(workspaceName)
                    ? "Cold workspace"
                    : $"{workspaceName.Trim()} template";
                var newName = await _toastWindow
                    .ShowInputPromptAsync(
                        "Persist as cold workspace",
                        "Save this hot workspace as a reusable cold template.",
                        "Workspace name",
                        defaultName,
                        fieldLabel: "Cold workspace name",
                        confirmButtonText: "Persist",
                        subtitle: workspaceName)
                    .ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return;
                }

                var cold = await workspaceProvider.PersistHotWorkspaceAsync(
                        normalizedWorkspaceId,
                        newName.Trim(),
                        CancellationToken.None)
                    .ConfigureAwait(true);
                if (cold == null)
                {
                    _notificationService.ShowError("Workspace is not a hot instance.");
                    return;
                }

                await RefreshDynamicProviderGroupsAsync(CancellationToken.None).ConfigureAwait(true);
                _notificationService.ShowSuccess($"Persisted cold workspace '{cold.Name}'.");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to persist workspace: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task HideRuntimeWorkspaceAsync(
            string workspaceId,
            string workspaceName)
        {
            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return;
            }

            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    _notificationService.ShowError("Workspace provider is unavailable.");
                    return;
                }

                var hidden = workspaceProvider.HideWorkspaceWindows(normalizedWorkspaceId);
                var label = string.IsNullOrWhiteSpace(workspaceName) ? normalizedWorkspaceId : workspaceName.Trim();
                if (hidden > 0)
                {
                    _notificationService.ShowSuccess($"Hidden {hidden} window(s) for '{label}'.");
                }
                else
                {
                    _notificationService.ShowWarning($"No live windows found for '{label}'.");
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to hide workspace windows: " + ex.Message);
            }

            await RefreshDynamicProviderGroupsAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async System.Threading.Tasks.Task KillRuntimeWorkspaceAsync(
            string workspaceId,
            string workspaceName)
        {
            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return;
            }

            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    _notificationService.ShowError("Workspace provider is unavailable.");
                    return;
                }

                var killed = workspaceProvider.KillWorkspaceWindows(normalizedWorkspaceId);
                var label = string.IsNullOrWhiteSpace(workspaceName) ? normalizedWorkspaceId : workspaceName.Trim();
                if (killed > 0)
                {
                    _notificationService.ShowSuccess($"Killed {killed} process(es) for '{label}'.");
                }
                else
                {
                    _notificationService.ShowWarning($"No live processes found for '{label}'.");
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to kill workspace windows: " + ex.Message);
            }

            await RefreshDynamicProviderGroupsAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async System.Threading.Tasks.Task RenameRuntimeWorkspaceAsync(
            string workspaceId,
            string currentWorkspaceName)
        {
            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return;
            }

            var currentName = string.IsNullOrWhiteSpace(currentWorkspaceName)
                ? normalizedWorkspaceId
                : currentWorkspaceName.Trim();

            try
            {
                if (!_providerRuntime.TryGetProvider(WorkspaceProviderId, out var provider) ||
                    provider is not WorkspaceProvider workspaceProvider)
                {
                    _notificationService.ShowError("Workspace provider is unavailable.");
                    return;
                }

                var newName = await _toastWindow
                    .ShowInputPromptAsync(
                        "Rename workspace",
                        "Enter a new name for this workspace.",
                        "Workspace name",
                        currentName,
                        fieldLabel: "Workspace name",
                        confirmButtonText: "Rename",
                        subtitle: currentName)
                    .ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return;
                }

                var normalizedName = newName.Trim();
                if (string.Equals(normalizedName, currentName, StringComparison.Ordinal))
                {
                    return;
                }

                var renamed = await workspaceProvider.RenameWorkspaceAsync(
                        normalizedWorkspaceId,
                        normalizedName,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                if (renamed == null)
                {
                    _notificationService.ShowError("Workspace was not found.");
                    return;
                }

                await RefreshDynamicProviderGroupsAsync(CancellationToken.None).ConfigureAwait(true);
                _notificationService.ShowSuccess($"Renamed workspace to '{renamed.Name}'.");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("Failed to rename workspace: " + ex.Message);
            }
        }

        private async void OnQuickSnapshotClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                AppLogger.LogInfo(
                    $"UI.Click: SnapshotButton clicked. enabled={btn.IsEnabled}, loaded={btn.IsLoaded}, snapshotInProgress={_snapshotInProgress}");
                await HandleQuickSnapshotAsync(btn).ConfigureAwait(true);
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
                });
            };
            _settingsWindow.Activate();
        }

        private void CloseSettingsWindow()
        {
            if (_settingsWindow == null)
            {
                return;
            }

            var settingsWindow = _settingsWindow;
            settingsWindow.Close();
        }
    }
}
