// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services;
using TopToolbar.Services.Workspaces;
using Timer = System.Timers.Timer;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel : ObservableObject, IDisposable
    {
        private readonly ToolbarConfigService _service;
        private readonly Timer _saveDebounce = new(300) { AutoReset = false };
        private DispatcherQueue _dispatcher;
        private bool _suppressGeneralSave;

        public SettingsViewModel(ToolbarConfigService service)
        {
            _service = service;
            _workspaceDefinitionStore = new WorkspaceDefinitionStore(null, _workspaceConfigStore);
            _saveDebounce.Elapsed += async (s, e) =>
            {
                await SaveAsync();
            };

            Groups.CollectionChanged += Groups_CollectionChanged;
            WorkspaceButtons.CollectionChanged += WorkspaceButtons_CollectionChanged;
        }

        public async Task LoadAsync(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
            var toolbarConfig = await _service.LoadAsync();
            var workspaceConfig = await _workspaceConfigStore.LoadAsync();
            var workspaceDefinitions = await _workspaceDefinitionStore.LoadAllAsync(CancellationToken.None);

            void Apply()
            {
                _suppressGeneralSave = true;

                try
                {
                    DisplayMode = toolbarConfig.DisplayMode;
                    TopBarEnabled = toolbarConfig.TopBarEnabled;
                    RadialMenuEnabled = toolbarConfig.RadialMenuEnabled;
                    RequireCtrlForTopBarTrigger = toolbarConfig.RequireCtrlForTopBarTrigger;
                    Theme = toolbarConfig.Theme;
                    SystemControlsEnabled = toolbarConfig.DefaultActions.SystemControlsEnabled;
                    MediaPlayPauseEnabled = toolbarConfig.DefaultActions.MediaPlayPause.Enabled;
                    LoadHotCorners(toolbarConfig.HotCorners);

                    Groups.Clear();
                    foreach (var g in toolbarConfig.Groups)
                    {
                        Groups.Add(g);
                    }

                    EnsureDefaultActionsGroupEntry();

                    if (SelectedGroup == null && Groups.Count > 0)
                    {
                        SelectedGroup = Groups[0];
                        SelectedButton = SelectedGroup.Buttons.FirstOrDefault();
                    }

                    _suppressWorkspaceSave = true;
                    try
                    {
                        LoadWorkspaceButtons(workspaceConfig, workspaceDefinitions);
                    }
                    finally
                    {
                        _suppressWorkspaceSave = false;
                    }
                }
                finally
                {
                    _suppressGeneralSave = false;
                }
            }

            if (dispatcher.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                var tcs = new TaskCompletionSource();
                dispatcher.TryEnqueue(() =>
                {
                    Apply();
                    tcs.SetResult();
                });
                await tcs.Task;
            }
        }

        public async Task SaveAsync()
        {
            // Ensure we mutate bound properties on UI thread
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource();
                _dispatcher.TryEnqueue(async () =>
                {
                    await SaveCoreAsync();
                    tcs.SetResult();
                });
                await tcs.Task;
                return;
            }

            await SaveCoreAsync();
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            if (_dispatcher == null || _dispatcher.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }))
            {
                tcs.SetException(new InvalidOperationException("Failed to enqueue work on the UI thread."));
            }

            return tcs.Task;
        }

        private async Task SaveCoreAsync()
        {
            // Ensure exe icons extracted before save (robustness if user hit Save quickly)
            AppLogger.LogInfo("SaveCoreAsync: begin icon extraction sweep");
            foreach (var g in Groups)
            {
                foreach (var b in g.Buttons)
                {
                    TryUpdateIconFromCommand(b);
                }
            }

            // Create config with only valid buttons (non-empty command)
            // Don't modify the UI Groups collection - just filter for saving
            // Preserve group Id to avoid duplication on reload
            var cfg = new ToolbarConfig
            {
                DisplayMode = TopBarEnabled || !RadialMenuEnabled
                    ? ToolbarDisplayMode.TopBar
                    : ToolbarDisplayMode.RadialMenu,
                TopBarEnabled = TopBarEnabled,
                RadialMenuEnabled = RadialMenuEnabled,
                RequireCtrlForTopBarTrigger = RequireCtrlForTopBarTrigger,
                Theme = Theme,
                DefaultActions = new DefaultActionsConfig
                {
                    SystemControlsEnabled = SystemControlsEnabled,
                    MediaPlayPause = new DefaultActionItemConfig
                    {
                        Enabled = MediaPlayPauseEnabled,
                    },
                },
                HotCorners = BuildHotCornersConfig(),
                Groups = Groups.Select(g => new ButtonGroup
                {
                    Id = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    IsEnabled = g.IsEnabled,
                    Layout = g.Layout,
                    Providers = g.Providers,
                    StaticActions = g.StaticActions,
                    Filter = g.Filter,
                    AutoRefresh = g.AutoRefresh,
                    Buttons = new System.Collections.ObjectModel.ObservableCollection<ToolbarButton>(
                        g.Buttons.Where(b => !string.IsNullOrWhiteSpace(b.Action?.Command))),
                })
                .Where(g => !string.Equals(g.Id, DefaultGroupsId, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            };

            await _service.SaveAsync(cfg);
            await SaveWorkspaceConfigAsync();
            AppLogger.LogInfo("SaveCoreAsync: configs saved");
        }

        private void ScheduleSave()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        public void Dispose()
        {
            _saveDebounce?.Stop();
            _saveDebounce?.Dispose();

            WorkspaceButtons.CollectionChanged -= WorkspaceButtons_CollectionChanged;
            foreach (var workspace in WorkspaceButtons.ToList())
            {
                UnhookWorkspaceButton(workspace);
            }

            Groups.CollectionChanged -= Groups_CollectionChanged;

            foreach (var g in Groups)
            {
                UnhookGroup(g);
            }

            GC.SuppressFinalize(this);
        }
    }
}
